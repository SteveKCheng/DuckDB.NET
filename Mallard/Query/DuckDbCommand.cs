﻿using Mallard.C_API;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Mallard;

/// <summary>
/// Prepared statement.
/// </summary>
public unsafe class DuckDbCommand : IDisposable
{
    private _duckdb_prepared_statement* _nativeStatement;
    private readonly int _numParams;
    private readonly Lock _mutex = new();
    private bool _isDisposed;

    #region Statement execution

    /// <summary>
    /// Execute the prepared statement and return the results (of the query).
    /// </summary>
    /// <returns>
    /// The results of the query execution.
    /// </returns>
    public DuckDbResult Execute()
    {
        duckdb_state status;
        duckdb_result nativeResult;
        lock (_mutex)
        {
            ThrowIfDisposed();

            // N.B. DuckDB captures the "client context" from the database connection
            // when NativeMethods.duckdb_prepare is called, and holds it with shared ownership.
            // Thus the connection object is not needed to execute the prepared statement,
            // (and the originating DuckDbConnection object does not have to be "locked").
            status = NativeMethods.duckdb_execute_prepared(_nativeStatement, out nativeResult);
        }

        return DuckDbResult.CreateFromQuery(status, ref nativeResult);
    }

    /// <summary>
    /// Execute the prepared statement, and report only the number of rows changed.
    /// </summary>
    /// <returns>
    /// The number of rows changed by the execution of the statement.
    /// The result is -1 if the statement did not change any rows, or is otherwise
    /// a statement or query for which DuckDB does not report the number of rows changed.
    /// </returns>
    public long ExecuteNonQuery()
    {
        duckdb_state status;
        duckdb_result nativeResult;
        lock (_mutex)
        {
            ThrowIfDisposed();
            status = NativeMethods.duckdb_execute_prepared(_nativeStatement, out nativeResult);
        }
        return DuckDbResult.ExtractNumberOfChangedRows(status, ref nativeResult);
    }

    #endregion

    /// <summary>
    /// Wrap the native object for a prepared statement from DuckDB.
    /// </summary>
    /// <param name="nativeConn">
    /// The native connection object that the prepared statement is associated with.
    /// </param>
    /// <param name="sql">
    /// The SQL statement to prepare. 
    /// </param>
    internal DuckDbCommand(_duckdb_connection* nativeConn, string sql)
    {
        var status = NativeMethods.duckdb_prepare(nativeConn, sql, out var nativeStatement);
        try
        {
            if (status == duckdb_state.DuckDBError)
            {
                var errorMessage = NativeMethods.duckdb_prepare_error(nativeStatement);
                throw new DuckDbException(errorMessage);
            }

            _numParams = (int)NativeMethods.duckdb_nparams(nativeStatement);
        }
        catch
        {
            NativeMethods.duckdb_destroy_prepare(ref nativeStatement);
            throw;
        }

        _nativeStatement = nativeStatement;
    }

    private void ThrowIfParamIndexOutOfRange(int index)
    {
        if (unchecked((uint)index - 1u >= (uint)_numParams))
            throw new IndexOutOfRangeException("Index of parameter is out of range. ");
    }

    /// <summary>
    /// The number of parameters in the prepared statement.
    /// </summary>
    /// <remarks>
    /// In this class, all indices of parameters are 1-based, i.e. the first parameter has index 1.
    /// This convention matches DuckDB's API and SQL syntax, where positional parameters
    /// are also 1-based.
    /// </remarks>
    public int ParameterCount => _numParams;

    /// <summary>
    /// Get the name of the parameter at the specified index.
    /// </summary>
    /// <param name="index">
    /// 1-based index of the parameter.
    /// </param>
    /// <returns>The name of the parameter in the SQL statement.  If the parameter
    /// has no name, the empty string is returned. </returns>
    public string GetParameterName(int index)
    {
        ThrowIfParamIndexOutOfRange(index);

        lock (_mutex)
        {
            ThrowIfDisposed();
            return NativeMethods.duckdb_parameter_name(_nativeStatement, index);
        }
    }

    public DuckDbBasicType GetParameterBasicType(int index)
    {
        ThrowIfParamIndexOutOfRange(index);

        lock (_mutex)
        {
            ThrowIfDisposed();
            return NativeMethods.duckdb_param_type(_nativeStatement, index);
        }
    }

    public int GetParameterIndexForName(string name)
    {
        long index;
        duckdb_state status;
        lock (_mutex)
        {
            ThrowIfDisposed();
            status = NativeMethods.duckdb_bind_parameter_index(_nativeStatement, out index, name);
        }
        if (status != duckdb_state.DuckDBSuccess)
            throw new KeyNotFoundException($"Parameter with the given name was not found. Name: {name}");
        return (int)index;
    }

    public void BindParameter<T>(int index, T value)
    {
        ThrowIfParamIndexOutOfRange(index);
        var nativeObject = DuckDbValue.CreateNativeObject(value);
        BindParameterInternal(index, ref nativeObject);
    }

    private void BindParameterInternal(int index, ref _duckdb_value* nativeValue)
    {
        if (nativeValue == null)
            throw new DuckDbException("Failed to create object wrapping value. ");

        try
        {
            duckdb_state status;
            lock (_mutex)
            {
                status = NativeMethods.duckdb_bind_value(_nativeStatement, index, nativeValue);
            }

            DuckDbException.ThrowOnFailure(status, "Could not bind specified value to parameter. ");
        }
        finally
        {
            NativeMethods.duckdb_destroy_value(ref nativeValue);
        }
    }

    #region Resource management

    private void DisposeImpl(bool disposing)
    {
        lock (_mutex)
        {
            if (_isDisposed)
                return;

            _isDisposed = true;
            NativeMethods.duckdb_destroy_prepare(ref _nativeStatement);
        }
    }

    ~DuckDbCommand()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        DisposeImpl(disposing: false);
    }

    public void Dispose()
    {
        DisposeImpl(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException("Cannot operate on this object after it has been disposed. ");
    }

    #endregion
}
