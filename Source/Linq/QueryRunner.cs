﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;

#if !SL4
using System.Threading.Tasks;
#endif

namespace LinqToDB.Linq
{
	using Data;
	using LinqToDB.Expressions;
	using SqlQuery;

	static class QueryRunner
	{
		#region Mapper

		class Mapper<T>
		{
			public Mapper(Expression<Func<IQueryRunner,IDataReader,T>> mapperExpression)
			{
				_expression = mapperExpression;
			}

			readonly Expression<Func<IQueryRunner,IDataReader,T>> _expression;
			         Expression<Func<IQueryRunner,IDataReader,T>> _mapperExpression;
			                    Func<IQueryRunner,IDataReader,T>  _mapper;

			bool _isFaulted;

			public IQueryRunner QueryRunner;

			public T Map(IQueryRunner queryRunner, IDataReader dataReader)
			{
				if (_mapper == null)
				{
					_mapperExpression = (Expression<Func<IQueryRunner,IDataReader,T>>)_expression.Transform(e =>
					{
						var ex = e as ConvertFromDataReaderExpression;
						return ex != null ? ex.Reduce(dataReader) : e;
					});

					var qr = QueryRunner;
					if (qr != null)
						qr.MapperExpression = _mapperExpression;

					_mapper = _mapperExpression.Compile();
				}

				try
				{
					return _mapper(queryRunner, dataReader);
				}
				catch (FormatException ex)
				{
					if (_isFaulted)
						throw;

#if !SILVERLIGHT && !NETFX_CORE
					if (DataConnection.TraceSwitch.TraceInfo)
						DataConnection.WriteTraceLine(
							"Mapper has switched to slow mode. Mapping exception: " + ex.Message,
							DataConnection.TraceSwitch.DisplayName);
#endif

					_isFaulted = true;

					var qr = QueryRunner;
					if (qr != null)
						qr.MapperExpression = _mapperExpression;

					return (_mapper = _expression.Compile())(queryRunner, dataReader);
				}
				catch (InvalidCastException ex)
				{
					if (_isFaulted)
						throw;

#if !SILVERLIGHT && !NETFX_CORE
					if (DataConnection.TraceSwitch.TraceInfo)
						DataConnection.WriteTraceLine(
							"Mapper has switched to slow mode. Mapping exception: " + ex.Message,
							DataConnection.TraceSwitch.DisplayName);
#endif

					_isFaulted = true;

					var qr = QueryRunner;
					if (qr != null)
						qr.MapperExpression = _mapperExpression;

					return (_mapper = _expression.Compile())(queryRunner, dataReader);
				}
			}
		}

		#endregion

		static void FinalizeQuery(Query query)
		{
			foreach (var sql in query.Queries)
			{
				sql.SelectQuery = query.SqlOptimizer.Finalize(sql.SelectQuery);
				sql.Parameters  = sql.Parameters
					.Select (p => new { p, idx = sql.SelectQuery.Parameters.IndexOf(p.SqlParameter) })
					.OrderBy(p => p.idx)
					.Select (p => p.p)
					.ToList();
			}
		}

		static void ClearParameters(Query query)
		{
			foreach (var q in query.Queries)
				foreach (var sqlParameter in q.Parameters)
					sqlParameter.Expression = null;
		}

		static int GetParameterIndex(Query query, ISqlExpression parameter)
		{
			var parameters = query.Queries[0].Parameters;

			for (var i = 0; i < parameters.Count; i++)
			{
				var p = parameters[i].SqlParameter;

				if (p == parameter)
					return i;
			}

			throw new InvalidOperationException();
		}

		static Tuple<
			Func<Query,QueryContext,IDataContextEx,Mapper<T>,Expression,object[],int,IEnumerable<T>>,
			Func<Expression,object[],int>,
			Func<Expression,object[],int>>
			GetExecuteQuery<T>(
				Query query,
				Func<Query,QueryContext,IDataContextEx,Mapper<T>,Expression,object[],int,IEnumerable<T>> queryFunc)
		{
			FinalizeQuery(query);

			if (query.Queries.Count != 1)
				throw new InvalidOperationException();

			Func<Expression,object[],int> skip = null, take = null;

			var select = query.Queries[0].SelectQuery.Select;

			if (select.SkipValue != null && !query.SqlProviderFlags.GetIsSkipSupportedFlag(query.Queries[0].SelectQuery))
			{
				var q = queryFunc;

				var value = select.SkipValue as SqlValue;
				if (value != null)
				{
					var n = (int)((IValueContainer)select.SkipValue).Value;

					if (n > 0)
					{
						queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Skip(n);
						skip  = (expr, ps) => n;
					}
				}
				else if (select.SkipValue is SqlParameter)
				{
					var i = GetParameterIndex(query, select.SkipValue);
					queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Skip((int)query.Queries[0].Parameters[i].Accessor(expr, ps));
					skip  = (expr,ps) => (int)query.Queries[0].Parameters[i].Accessor(expr, ps);
				}
			}

			if (select.TakeValue != null && !query.SqlProviderFlags.IsTakeSupported)
			{
				var q = queryFunc;

				var value = select.TakeValue as SqlValue;
				if (value != null)
				{
					var n = (int)((IValueContainer)select.TakeValue).Value;

					if (n > 0)
					{
						queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Take(n);
						take  = (expr, ps) => n;
					}
				}
				else if (select.TakeValue is SqlParameter)
				{
					var i = GetParameterIndex(query, select.TakeValue);
					queryFunc = (qq, qc, db, mapper, expr, ps, qn) => q(qq, qc, db, mapper, expr, ps, qn).Take((int)query.Queries[0].Parameters[i].Accessor(expr, ps));
					take  = (expr,ps) => (int)query.Queries[0].Parameters[i].Accessor(expr, ps);
				}
			}

			return Tuple.Create(queryFunc, skip, take);
		}

		static IEnumerable<T> ExecuteQuery<T>(
			Query          query,
			QueryContext   queryContext,
			IDataContextEx dataContext,
			Mapper<T>      mapper,
			Expression     expression,
			object[]       ps,
			int            queryNumber)
		{
			if (queryContext == null)
				queryContext = new QueryContext(dataContext, expression, ps);

			using (var runner = dataContext.GetQueryRunner(query, queryNumber, expression, ps))
			{
				runner.QueryContext = queryContext;
				runner.DataContext  = dataContext;

				try
				{
					mapper.QueryRunner = runner;

					using (var dr = runner.ExecuteReader())
					{
						while (dr.Read())
						{
							yield return mapper.Map(runner, dr);
							runner.RowsCount++;
						}
					}
				}
				finally
				{
					mapper.QueryRunner = null;
				}
			}
		}

#if !NOASYNC

		static async Task ExecuteQueryAsync<T>(
			Query                         query,
			QueryContext                  queryContext,
			IDataContextEx                dataContext,
			Mapper<T>                     mapper,
			Expression                    expression,
			object[]                      ps,
			int                           queryNumber,
			Action<T>                     action,
			Func<Expression,object[],int> skipAction,
			Func<Expression,object[],int> takeAction,
			CancellationToken             cancellationToken,
			TaskCreationOptions           options)
		{
			if (queryContext == null)
				queryContext = new QueryContext(dataContext, expression, ps);

			using (var runner = dataContext.GetQueryRunner(query, queryNumber, expression, ps))
			{
				Func<IDataReader,T> m = dr => mapper.Map(runner, dr);

				runner.SkipAction   = skipAction != null ? () => skipAction(expression, ps) : null as Func<int>;
				runner.TakeAction   = takeAction != null ? () => takeAction(expression, ps) : null as Func<int>;
				runner.QueryContext = queryContext;
				runner.DataContext  = dataContext;

				try
				{
					mapper.QueryRunner = runner;

					var dr = await runner.ExecuteReaderAsync(cancellationToken, options);
					await dr.QueryForEachAsync(m, r => { action(r); runner.RowsCount++; }, cancellationToken);
				}
				finally
				{
					mapper.QueryRunner = null;
				}
			}
		}

#endif

		static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<IQueryRunner,IDataReader,T>> expression)
		{
			var executeQuery = GetExecuteQuery<T>(query, ExecuteQuery);

			ClearParameters(query);

			var mapper   = new Mapper<T>(expression);
			var runQuery = executeQuery.Item1;

			query.GetIEnumerable = (ctx,db,expr,ps) => runQuery(query, ctx, db, mapper, expr, ps, 0);

#if !NOASYNC

			var skipAction = executeQuery.Item2;
			var takeAction = executeQuery.Item3;

			query.GetForEachAsync = (expressionQuery,ctx,db,expr,ps,action,token,options) =>
				ExecuteQueryAsync(query, ctx, db, mapper, expr, ps, 0, action, skipAction, takeAction, token, options);

#endif
		}

		static readonly PropertyInfo _queryContextInfo = MemberHelper.PropertyOf<IQueryRunner>( p => p.QueryContext);
		static readonly PropertyInfo _dataContextInfo  = MemberHelper.PropertyOf<IQueryRunner>( p => p.DataContext);
		static readonly PropertyInfo _expressionInfo   = MemberHelper.PropertyOf<IQueryRunner>( p => p.Expression);
		static readonly PropertyInfo _parametersnfo    = MemberHelper.PropertyOf<IQueryRunner>( p => p.Parameters);
		static readonly PropertyInfo _rowsCountnfo     = MemberHelper.PropertyOf<IQueryRunner>( p => p.RowsCount);

		public static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<QueryContext,IDataContext,IDataReader,Expression,object[],T>> expression)
		{
			var queryRunnerParam = Expression.Parameter(typeof(IQueryRunner), "qr");
			var dataReaderParam  = Expression.Parameter(typeof(IDataReader),  "dr");

			var l =
				Expression.Lambda<Func<IQueryRunner,IDataReader,T>>(
					Expression.Invoke(
						expression, new[]
						{
							Expression.Property(queryRunnerParam, _queryContextInfo) as Expression,
							Expression.Property(queryRunnerParam, _dataContextInfo),
							dataReaderParam,
							Expression.Property(queryRunnerParam, _expressionInfo),
							Expression.Property(queryRunnerParam, _parametersnfo),
						}),
					queryRunnerParam,
					dataReaderParam);

			SetRunQuery(query, l);
		}

		public static void SetRunQuery<T>(
			Query<T> query,
			Expression<Func<QueryContext,IDataContext,IDataReader,Expression,object[],int,T>> expression)
		{
			var queryRunnerParam = Expression.Parameter(typeof(IQueryRunner), "qr");
			var dataReaderParam  = Expression.Parameter(typeof(IDataReader),  "dr");

			var l =
				Expression.Lambda<Func<IQueryRunner,IDataReader,T>>(
					Expression.Invoke(
						expression, new[]
						{
							Expression.Property(queryRunnerParam, _queryContextInfo) as Expression,
							Expression.Property(queryRunnerParam, _dataContextInfo),
							dataReaderParam,
							Expression.Property(queryRunnerParam, _expressionInfo),
							Expression.Property(queryRunnerParam, _parametersnfo),
							Expression.Property(queryRunnerParam, _rowsCountnfo),
						}),
					queryRunnerParam,
					dataReaderParam);

			SetRunQuery(query, l);
		}
	}
}
