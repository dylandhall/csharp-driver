//
//      Copyright (C) 2012 DataStax Inc.
//
//   Licensed under the Apache License, Version 2.0 (the "License");
//   you may not use this file except in compliance with the License.
//   You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//   Unless required by applicable law or agreed to in writing, software
//   distributed under the License is distributed on an "AS IS" BASIS,
//   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//   See the License for the specific language governing permissions and
//   limitations under the License.
//
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Linq.Expressions;
using System.Collections;

namespace Cassandra.Data.Linq
{
    internal enum ParsePhase { None, Select, What, Condition, SelectBinding, Take, OrderBy, OrderByDescending };

    public class CqlLinqNotSupportedException : NotSupportedException
    {
        public Expression Expression { get; private set; }
        internal CqlLinqNotSupportedException(Expression expression, ParsePhase parsePhase)
            : base(string.Format("The expression {0} = [{1}] is not supported in {2} parse phase.",
                        expression.NodeType.ToString(), expression.ToString(), parsePhase.ToString()))
        {
            Expression = expression;
        }
    }

    public class CqlArgumentException : ArgumentException
    {
        internal CqlArgumentException(string message)
            : base(message)
        { }
    }

    internal class CqlStringTool
    {
        List<object> srcvalues = new List<object>();

        public string FillWithEncoded(string pure)
        {
            if (srcvalues.Count == 0)
                return pure;

            var sb = new StringBuilder();
            var parts = pure.Split('\0');

            for (int i = 0; i < parts.Length - 1; i += 2)
            {
                sb.Append(parts[i]);
                var idx = int.Parse(parts[i + 1]);
                sb.Append(CqlQueryTools.Encode(srcvalues[idx]));
            }
            sb.Append(parts.Last());
            return sb.ToString();
        }

        public string FillWithValues(string pure, out object[] values)
        {
            if (srcvalues.Count == 0)
            {
                values = null;
                return pure;
            }

            var sb = new StringBuilder();
            var objs = new List<object>();
            var parts = pure.Split('\0');

            for (int i = 0; i < parts.Length - 1; i += 2)
            {
                sb.Append(parts[i]);
                var idx = int.Parse(parts[i + 1]);
                objs.Add(srcvalues[idx]);
                sb.Append(" ? ");
            }
            sb.Append(parts.Last());
            values = objs.ToArray();
            return sb.ToString();
        }

        public string AddValue(object val)
        {
            srcvalues.Add(val);
            return "\0" + (srcvalues.Count - 1).ToString() + "\0";
        }
    }

    internal class CqlExpressionVisitor : ExpressionVisitor
    {
        public StringBuilder WhereClause = new StringBuilder();
        public StringBuilder UpdateIfClause = new StringBuilder();
        public string QuotedTableName;

        public Dictionary<string, string> Alter = new Dictionary<string, string>();
        public Dictionary<string, Tuple<string, object, int>> Mappings = new Dictionary<string, Tuple<string, object, int>>();
        public HashSet<string> SelectFields = new HashSet<string>();
        public List<string> OrderBy = new List<string>();

        public int Limit = 0;
        public bool AllowFiltering = false;

        VisitingParam<ParsePhase> phasePhase = new VisitingParam<ParsePhase>(ParsePhase.None);
        VisitingParam<string> currentBindingName = new VisitingParam<string>(null);
        VisitingParam<string> binaryExpressionTag = new VisitingParam<string>(null);
        VisitingParam<StringBuilder> currentConditionBuilder;

        CqlStringTool cqlTool = new CqlStringTool();

        public CqlExpressionVisitor()
        {
            currentConditionBuilder = new VisitingParam<StringBuilder>(WhereClause);
        }

        public string GetSelect(out object[] values, bool withValues = true)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("SELECT ");
            sb.Append(SelectFields.Count == 0 ? "*" : string.Join(", ", from f in SelectFields select Alter[f].QuoteIdentifier()));

            sb.Append(" FROM ");
            sb.Append(QuotedTableName);

            if (WhereClause.Length > 0)
            {
                sb.Append(" WHERE ");
                sb.Append(WhereClause);
            }

            if (OrderBy.Count > 0)
            {
                sb.Append(" ORDER BY ");
                sb.Append(string.Join(", ", OrderBy));
            }

            if (Limit > 0)
            {
                sb.Append(" LIMIT ");
                sb.Append(Limit);
            }

            if (AllowFiltering)
                sb.Append(" ALLOW FILTERING");

            if (withValues)
                return cqlTool.FillWithValues(sb.ToString(), out values);
            else
            {
                values = null;
                return cqlTool.FillWithEncoded(sb.ToString());
            }
        }



        public string GetDelete(out object[] values, DateTimeOffset? timestamp, bool withValues = true)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("DELETE FROM ");
            sb.Append(QuotedTableName);
            if (timestamp != null)
            {
                sb.Append(" USING TIMESTAMP ");
                sb.Append(Convert.ToInt64(Math.Floor((timestamp.Value - CqlQueryTools.UnixStart).TotalMilliseconds)));
                sb.Append(" ");
            }

            if (WhereClause.Length > 0)
            {
                sb.Append(" WHERE ");
                sb.Append(WhereClause);
            }
            if (SelectFields.Count > 0)
                throw new CqlArgumentException("Unable to delete entity partially");

            if (withValues)
                return cqlTool.FillWithValues(sb.ToString(), out values);
            else
            {
                values = null;
                return cqlTool.FillWithEncoded(sb.ToString());
            }
        }

        public string GetUpdate(out object[] values, int? ttl, DateTimeOffset? timestamp,bool withValues = true)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("UPDATE ");
            sb.Append(QuotedTableName);
            if (ttl != null || timestamp != null)
            {
                sb.Append(" USING ");
            }
            if (ttl != null)
            {
                sb.Append("TTL ");
                sb.Append(ttl.Value);
                if (timestamp != null)
                    sb.Append(" AND ");
            }
            if (timestamp != null)
            {
                sb.Append("TIMESTAMP ");
                sb.Append(Convert.ToInt64(Math.Floor((timestamp.Value - CqlQueryTools.UnixStart).TotalMilliseconds)));
                sb.Append(" ");
            }
            sb.Append(" SET ");

			var setStatements = new List<string>();

			foreach (var mapping in Mappings)
			{
				var o = mapping.Value.Item2;
                if (o != null)
                {
                    var val = (object)null;
                    var propsOrField = o.GetType().GetPropertiesOrFields().SingleOrDefault(pf => pf.Name == mapping.Value.Item1);

                    if (o.GetType().IsPrimitive || propsOrField == null)
                        val = o;
                    else
                        val = propsOrField.GetValueFromPropertyOrField(o);

                    if (!Alter.ContainsKey(mapping.Key))
                        throw new CqlArgumentException("Unknown column: " + mapping.Key);
                    setStatements.Add(Alter[mapping.Key].QuoteIdentifier() + " = " + cqlTool.AddValue(val));
                }
                else
                {
                    if (!Alter.ContainsKey(mapping.Key))
                        throw new CqlArgumentException("Unknown column: " + mapping.Key);
                    setStatements.Add(Alter[mapping.Key].QuoteIdentifier() + " = NULL");
                }
            }

            if (setStatements.Count == 0)
                throw new CqlArgumentException("Nothing to update");
			sb.Append(String.Join(", ", setStatements));
	
            if (WhereClause.Length > 0)
            {
                sb.Append(" WHERE ");
                sb.Append(WhereClause);
            }

            if (UpdateIfClause.Length > 0)
            {
                sb.Append(" IF ");
                sb.Append(UpdateIfClause);
            }

            if (withValues)
                return cqlTool.FillWithValues(sb.ToString(), out values);
            else
            {
                values = null;
                return cqlTool.FillWithEncoded(sb.ToString());
            }
        }

        public string GetCount(out object[] values, bool withValues = true)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("SELECT count(*) FROM ");
            sb.Append(QuotedTableName);

            if (WhereClause.Length > 0)
            {
                sb.Append(" WHERE ");
                sb.Append(WhereClause);
            }

            if (Limit > 0)
            {
                sb.Append(" LIMIT ");
                sb.Append(Limit);
            }

            if (withValues)
                return cqlTool.FillWithValues(sb.ToString(), out values);
            else
            {
                values = null;
                return cqlTool.FillWithEncoded(sb.ToString());
            }
        }

        public void Evaluate(Expression expression)
        {
            this.Visit(expression);
        }

        protected override Expression VisitMemberInit(MemberInitExpression node)
        {
            if (phasePhase.get() == ParsePhase.SelectBinding)
            {
                foreach (var binding in node.Bindings)
                {
                    if (binding is MemberAssignment)
                    {
                        using (currentBindingName.set(binding.Member.Name))
                            this.Visit((binding as MemberAssignment).Expression);
                    }
                }
                return node;
            }
            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }

        protected override Expression VisitLambda<T>(Expression<T> node)
        {
            if (phasePhase.get() == ParsePhase.What)
            {
                using (phasePhase.set(ParsePhase.SelectBinding))
                using (currentBindingName.set(node.Parameters[0].Name))
                    this.Visit(node.Body);
                return node;
            }
            return base.VisitLambda<T>(node);
        }

        protected override Expression VisitNew(NewExpression node)
        {
            if (phasePhase.get() == ParsePhase.SelectBinding)
            {
                if (node.Members != null)
                {
                    for (int i = 0; i < node.Members.Count; i++)
                    {
                        var binding = node.Arguments[i];
                        if (binding.NodeType == ExpressionType.Parameter)
                            throw new CqlLinqNotSupportedException(binding, phasePhase.get());

                        using (currentBindingName.set(node.Members[i].Name))
                            this.Visit(binding);
                    }
                }
                return node;
            }
            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.Name == "Select")
            {
                this.Visit(node.Arguments[0]);

                using(phasePhase.set(ParsePhase.What))
                    this.Visit(node.Arguments[1]);

                return node;
            }
            else if (node.Method.Name == "Where")
            {
                this.Visit(node.Arguments[0]);

                using (phasePhase.set(ParsePhase.Condition))
                {
                    if (WhereClause.Length != 0)
                        WhereClause.Append(" AND ");
                    this.Visit(node.Arguments[1]);
                }
                return node;
            }
            else if (node.Method.Name == "UpdateIf")
            {
                this.Visit(node.Arguments[0]);

                using (phasePhase.set(ParsePhase.Condition))
                {
                    if (UpdateIfClause.Length != 0)
                        UpdateIfClause.Append(" AND ");
                    using (currentConditionBuilder.set(UpdateIfClause))
                        this.Visit(node.Arguments[1]);
                }
                return node;
            }
            else if (node.Method.Name == "Take")
            {
                this.Visit(node.Arguments[0]);
                using (phasePhase.set(ParsePhase.Take))
                    this.Visit(node.Arguments[1]);
                return node;
            }
            else if (node.Method.Name == "OrderBy" || node.Method.Name == "ThenBy")
            {
                this.Visit(node.Arguments[0]);
                using (phasePhase.set(ParsePhase.OrderBy))
                    this.Visit(node.Arguments[1]);
                return node;
            }
            else if (node.Method.Name == "OrderByDescending" || node.Method.Name == "ThenByDescending")
            {
                this.Visit(node.Arguments[0]);
                using (phasePhase.set(ParsePhase.OrderByDescending))
                    this.Visit(node.Arguments[1]);
                return node;
            }
            else if (node.Method.Name == "FirstOrDefault" || node.Method.Name == "First")
            {
                this.Visit(node.Arguments[0]);
                if (node.Arguments.Count == 3)
                {
                    using (phasePhase.set(ParsePhase.Condition))
                        this.Visit(node.Arguments[2]);
                }
                Limit = 1;
                return node;
            }

            if (phasePhase.get() == ParsePhase.Condition)
            {
                if (node.Method.Name == "Contains")
                {
                    Expression what = null;
                    Expression inp = null;
                    if (node.Object == null)
                    {
                        what = node.Arguments[1];
                        inp = node.Arguments[0];
                    }
                    else
                    {
                        what = node.Arguments[0];
                        inp = node.Object;
                    }
                    this.Visit(what);
                    currentConditionBuilder.get().Append(" IN (");
                    var values = (IEnumerable)Expression.Lambda(inp).Compile().DynamicInvoke();
                    bool first = false;
                    foreach (var obj in values)
                    {
                        if (!first)
                            first = true;
                        else
                            currentConditionBuilder.get().Append(", ");
                        currentConditionBuilder.get().Append(cqlTool.AddValue(obj));
                    }
                    if (!first)
                        throw new CqlArgumentException("Collection " + inp.ToString() + " is empty.");
                    currentConditionBuilder.get().Append(")");
                    return node;
                }
                else if (node.Method.Name == "CompareTo")
                {
                    this.Visit(node.Object);
                    currentConditionBuilder.get().Append(" " + binaryExpressionTag.get() + " ");
                    this.Visit(node.Arguments[0]);
                    return node;
                }
                else if(node.Method.Name == "Equals")
                {
                    this.Visit(node.Object);
                    currentConditionBuilder.get().Append(" = ");
                    this.Visit(node.Arguments[0]);
                    return node;
                }
                else if (node.Type.Name == "CqlToken")
                {
                    currentConditionBuilder.get().Append("token(");
                    var exprs = node.Arguments;
                    this.Visit(exprs.First());
                    foreach (var e in exprs.Skip(1))
                    {
                        currentConditionBuilder.get().Append(", ");
                        this.Visit(e);
                    }
                    currentConditionBuilder.get().Append(")");
                    return node;
                }
                else
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    currentConditionBuilder.get().Append(cqlTool.AddValue(val));
                    return node;
                }

            }

            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }

        static readonly Dictionary<ExpressionType, string> CQLTags = new Dictionary<ExpressionType, string>()
        {
            {ExpressionType.Not,"NOT"},
            {ExpressionType.And,"AND"},
            {ExpressionType.AndAlso,"AND"},
			{ExpressionType.Equal,"="},
			{ExpressionType.NotEqual,"<>"},
			{ExpressionType.GreaterThan,">"},
			{ExpressionType.GreaterThanOrEqual,">="},
			{ExpressionType.LessThan,"<"},
			{ExpressionType.LessThanOrEqual,"<="}
        };

        static readonly HashSet<ExpressionType> CQLUnsupTags = new HashSet<ExpressionType>()
        {
            {ExpressionType.Or},
            {ExpressionType.OrElse},
        };

        static readonly Dictionary<ExpressionType, ExpressionType> CQLInvTags = new Dictionary<ExpressionType, ExpressionType>()
        {
			{ExpressionType.Equal,ExpressionType.Equal},
			{ExpressionType.NotEqual,ExpressionType.NotEqual},
			{ExpressionType.GreaterThan,ExpressionType.LessThan},
			{ExpressionType.GreaterThanOrEqual,ExpressionType.LessThanOrEqual},
			{ExpressionType.LessThan,ExpressionType.GreaterThan},
			{ExpressionType.LessThanOrEqual,ExpressionType.GreaterThanOrEqual}
        };
        
        private static Expression DropNullableConversion(Expression node)
        {
            if (node is UnaryExpression && node.NodeType == ExpressionType.Convert && node.Type.IsGenericType && node.Type.Name.CompareTo("Nullable`1") == 0)
                return (node as UnaryExpression).Operand;
            return node;
        }

        protected override Expression VisitUnary(UnaryExpression node)
        {
            if (phasePhase.get() == ParsePhase.Condition)
            {
                if (CQLTags.ContainsKey(node.NodeType))
                {
                    currentConditionBuilder.get().Append(CQLTags[node.NodeType] + " (");
                    this.Visit(DropNullableConversion(node.Operand));
                    currentConditionBuilder.get().Append(")");
                }
                else
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    currentConditionBuilder.get().Append(cqlTool.AddValue(val));
                }
                return node;
            }
            if (phasePhase.get() == ParsePhase.SelectBinding)
            {
                if (node.NodeType == ExpressionType.Convert && node.Type.Name == "Nullable`1")
                {
                    return this.Visit(node.Operand);
                }
            }
            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }

        private bool IsCompareTo(Expression node)
        {
            if (node.NodeType == ExpressionType.Call)
                if ((node as MethodCallExpression).Method.Name == "CompareTo")
                    return true;
            return false;
        }

        private bool IsZero(Expression node)
        {
            if (node.NodeType == ExpressionType.Constant)
                if ((node as ConstantExpression).Value is int)
                    if (((int)(node as ConstantExpression).Value) == 0)
                        return true;
            return false;
        }

        protected override Expression VisitBinary(BinaryExpression node)
        {
            if (phasePhase.get() == ParsePhase.Condition)
            {
                if (CQLTags.ContainsKey(node.NodeType))
                {
                    if (IsCompareTo(node.Left))
                    {
                        if (IsZero(node.Right))
                        {
                            using (binaryExpressionTag.set(CQLTags[node.NodeType]))
                                this.Visit(node.Left);
                            return node;
                        }
                    }
                    else if (IsCompareTo(node.Right))
                    {
                        if (IsZero(node.Left))
                        {
                            using (binaryExpressionTag.set(CQLTags[CQLInvTags[node.NodeType]]))
                                this.Visit(node.Right);
                            return node;
                        }
                    }
                    else
                    {
                        this.Visit(DropNullableConversion(node.Left));
                        currentConditionBuilder.get().Append(" " + CQLTags[node.NodeType] + " ");
                        this.Visit(DropNullableConversion(node.Right));
                        return node;
                    }
                }
                else if (!CQLUnsupTags.Contains(node.NodeType))
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    currentConditionBuilder.get().Append(cqlTool.AddValue(val));
                    return node;
                }
            }
            else if (phasePhase.get() == ParsePhase.SelectBinding)
            {
                var val = Expression.Lambda(node).Compile().DynamicInvoke();
                if (Alter.ContainsKey(currentBindingName.get()))
                {
                    Mappings[currentBindingName.get()] = Tuple.Create(currentBindingName.get(), val, Mappings.Count);
                    SelectFields.Add(currentBindingName.get());
                }
                else
                {
                    Mappings[currentBindingName.get()] = Tuple.Create<string, object, int>(null, val, Mappings.Count);
                }
                return node;
            }
            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if (node.Value is ITable)
            {
                var table = (node.Value as ITable);
                QuotedTableName = table.GetQuotedTableName();
                AllowFiltering = table.GetEntityType().GetCustomAttributes(typeof(AllowFilteringAttribute), false).Any();
                
                var props = table.GetEntityType().GetPropertiesOrFields();
                foreach (var prop in props)
                {
                    var memName = CqlQueryTools.CalculateMemberName(prop);
                    Alter[prop.Name] = memName;
                }
                return node;
            }
            else if (phasePhase.get() == ParsePhase.Condition)
            {
                currentConditionBuilder.get().Append(cqlTool.AddValue(node.Value));
                return node;
            }
            else if (phasePhase.get() == ParsePhase.SelectBinding)
            {
                if (Alter.ContainsKey(currentBindingName.get()))
                {
                    Mappings[currentBindingName.get()] = Tuple.Create(currentBindingName.get(), node.Value, Mappings.Count);
                    SelectFields.Add(currentBindingName.get());
                }
                else
                {
                    Mappings[currentBindingName.get()] = Tuple.Create<string, object, int>(null, node.Value, Mappings.Count);
                }
                return node;
            }
            else if (phasePhase.get() == ParsePhase.Take)
            {
                Limit = (int)node.Value;
                return node;
            }
            else if (phasePhase.get() == ParsePhase.OrderBy || phasePhase.get() == ParsePhase.OrderByDescending)
            {
                OrderBy.Add(Alter[(string)node.Value].QuoteIdentifier() + (phasePhase.get() == ParsePhase.OrderBy ? " ASC" : " DESC"));
                return node;
            }
            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }

        protected override Expression VisitMember(MemberExpression node)
        {
            if (phasePhase.get() == ParsePhase.Condition)
            {
                if (node.Expression == null)
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    currentConditionBuilder.get().Append(cqlTool.AddValue(val));
                    return node;
                }
                else if (node.Expression.NodeType == ExpressionType.Parameter)
                {
                    currentConditionBuilder.get().Append(Alter[node.Member.Name].QuoteIdentifier());
                    return node;
                }
                else if (node.Expression.NodeType == ExpressionType.Constant)
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    if (val is CqlToken)
                    {
                        currentConditionBuilder.get().Append("token(");
                        currentConditionBuilder.get().Append(cqlTool.AddValue((val as CqlToken).Values.First()));
                        foreach (var e in (val as CqlToken).Values.Skip(1))
                        {
                            currentConditionBuilder.get().Append(", ");
                            currentConditionBuilder.get().Append(cqlTool.AddValue(e));
                        }
                        currentConditionBuilder.get().Append(")");
                    }
                    else
                    {
                        currentConditionBuilder.get().Append(cqlTool.AddValue(val));
                    }
                    return node;
                }
                else if (node.Expression.NodeType == ExpressionType.MemberAccess)
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    currentConditionBuilder.get().Append(cqlTool.AddValue(val));
                    return node;
                }
            }
            else if (phasePhase.get() == ParsePhase.SelectBinding)
            {
                var name = node.Member.Name;
                if (node.Expression == null)
                {
                    var val = Expression.Lambda(node).Compile().DynamicInvoke();
                    Mappings[currentBindingName.get()] = Tuple.Create<string, object, int>(null, val, Mappings.Count);
                    return node;
                }
                else if (node.Expression.NodeType == ExpressionType.Constant || node.Expression.NodeType == ExpressionType.MemberAccess)
                {
                    if (Alter.ContainsKey(currentBindingName.get()))
                    {
                        var val = Expression.Lambda(node.Expression).Compile().DynamicInvoke();
                        Mappings[currentBindingName.get()] = Tuple.Create(name, val, Mappings.Count);
                        SelectFields.Add(name);
                    }
                    else
                    {
                        var val = Expression.Lambda(node).Compile().DynamicInvoke();
                        Mappings[currentBindingName.get()] = Tuple.Create<string,object,int>(null, val, Mappings.Count);
                    }
                    return node;
                }
                else if (node.Expression.NodeType == ExpressionType.Parameter)
                {
                    Mappings[currentBindingName.get()] = Tuple.Create<string, object, int>(name, name, Mappings.Count);
                    SelectFields.Add(name);
                    return node;
                }
            }
            else if (phasePhase.get() == ParsePhase.OrderBy || phasePhase.get() == ParsePhase.OrderByDescending)
            {
                var name = node.Member.Name;
                OrderBy.Add(Alter[(string)name].QuoteIdentifier() + (phasePhase.get() == ParsePhase.OrderBy ? " ASC" : " DESC"));

                if ((node.Expression is ConstantExpression))
                {
                    return node;
                }
                else if (node.Expression.NodeType == ExpressionType.Parameter)
                {
                    return node;
                }
            }
            throw new CqlLinqNotSupportedException(node, phasePhase.get());
        }
    }
}

