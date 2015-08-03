﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace ExpressMapper
{
    public abstract class TypeMapperBase<T, TN>
    {
        protected ParameterExpression DestFakeParameter = Expression.Parameter(typeof(TN), "dest");
        protected IMappingService MappingService { get; set; }

        #region Constructors

        protected TypeMapperBase(IMappingService service)
        {
            ResultExpressionList = new List<Expression>();
            PropertyCache = new Dictionary<string, Expression>();
            CustomPropertyCache = new Dictionary<string, Expression>();
            IgnoreMemberList = new List<string>();
            MappingService = service;
        }

        #endregion


        #region Properties
        
        protected ParameterExpression SourceParameter = Expression.Parameter(typeof(T), "src");
        protected List<Expression> ResultExpressionList { get; private set; }
        protected Func<T, TN, TN> ResultMapFunction { get; set; }
        protected List<string> IgnoreMemberList { get; private set; }
        protected Dictionary<string, Expression> PropertyCache { get; private set; }
        protected Dictionary<string, Expression> CustomPropertyCache { get; private set; }
        protected Action<T, TN> BeforeMapHandler { get; set; }
        protected Action<T, TN> AfterMapHandler { get; set; }
        protected Func<T, TN> ConstructorFunc { get; set; }
        protected Func<object, object> NonGenericMapFunc { get; set; }

        #endregion

        public abstract void Compile();

        public void AfterMap(Action<T, TN> afterMap)
        {
            AfterMapHandler = afterMap;
        }

        public List<Expression> GetMapExpressions()
        {
            Compile();
            return ResultExpressionList;
        }

        public Func<object, object> GetNonGenericMapFunc()
        {
            if (NonGenericMapFunc != null)
            {
                return NonGenericMapFunc;
            }

            var parameterExpression = Expression.Parameter(typeof(object), "src");
            var srcConverted = Expression.Convert(parameterExpression, typeof(T));
            var srcTypedExp = Expression.Variable(typeof(T), "srcTyped");
            var srcAssigned = Expression.Assign(srcTypedExp, srcConverted);

            var customGenericType = typeof(ITypeMapper<,>).MakeGenericType(typeof(T), typeof(TN));
            var castToCustomGeneric = Expression.Convert(Expression.Constant((ITypeMapper)this), customGenericType);
            var genVariable = Expression.Variable(customGenericType);
            var assignExp = Expression.Assign(genVariable, castToCustomGeneric);
            var methodInfo = customGenericType.GetMethod("MapTo", new[] { typeof(T), typeof(TN) });

            var mapCall = Expression.Call(genVariable, methodInfo, srcTypedExp, Expression.Default(typeof(TN)));
            var resultVarExp = Expression.Variable(typeof(object), "result");
            var convertToObj = Expression.Convert(mapCall, typeof(object));
            var assignResult = Expression.Assign(resultVarExp, convertToObj);

            var blockExpression = Expression.Block(new[] { srcTypedExp, genVariable, resultVarExp }, new Expression[] { srcAssigned, assignExp, assignResult, resultVarExp });
            var lambda = Expression.Lambda<Func<object, object>>(blockExpression, parameterExpression);
            NonGenericMapFunc = lambda.Compile();
            
            return NonGenericMapFunc;
        }

        protected void AutoMapProperty(PropertyInfo propertyGet, PropertyInfo propertySet)
        {
            var callSetPropMethod = Expression.Property(DestFakeParameter, propertySet);
            var callGetPropMethod = Expression.Property(SourceParameter, propertyGet);

            MapMember(callSetPropMethod, callGetPropMethod);
        }

        public void MapMember<TSourceMember, TDestMember>(Expression<Func<TN, TDestMember>> left, Expression<Func<T, TSourceMember>> right)
        {
            if (left == null)
            {
                throw new ArgumentNullException("left");
            }

            if (right == null)
            {
                throw new ArgumentNullException("right");
            }

            MapMember(left.Body as MemberExpression, right.Body);
        }

        private void MapMember(MemberExpression left, Expression right)
        {
            var mappingExpression = MappingService.GetMemberMappingExpression(left, right);
            CustomPropertyCache[left.Member.Name] = mappingExpression;
        }

        protected BinaryExpression GetDestionationVariable()
        {
            if (ConstructorFunc != null)
            {
                Expression<Func<T, TN>> customConstruct = t => ConstructorFunc(t);
                var invocationExpression = Expression.Invoke(customConstruct, SourceParameter);
                return Expression.Assign(DestFakeParameter, invocationExpression);
            }
            var createDestination = Expression.New(typeof(TN));
            return Expression.Assign(DestFakeParameter, createDestination);
        }

        protected void ProcessAutoProperties()
        {
            var getProps =
                typeof(T).GetProperties(BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.Public);
            var setProps =
                typeof(TN).GetProperties(BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.Public);

            foreach (var prop in getProps)
            {
                if (IgnoreMemberList.Contains(prop.Name) || CustomPropertyCache.ContainsKey(prop.Name))
                {
                    continue;
                }
                var setprop = setProps.FirstOrDefault(x => string.Equals(x.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

                if (setprop == null || !setprop.CanWrite || !setprop.GetSetMethod(true).IsPublic)
                {
                    IgnoreMemberList.Add(prop.Name);
                    continue;
                }

                AutoMapProperty(prop, setprop);
            }
        }
        public virtual void Instantiate(Func<T, TN> constructor)
        {
            ConstructorFunc = constructor;
        }

        public virtual void BeforeMap(Action<T, TN> beforeMap)
        {
            BeforeMapHandler = beforeMap;
        }

        public virtual void Ignore<TMember>(Expression<Func<TN, TMember>> left)
        {
            var memberExpression = left.Body as MemberExpression;
            IgnoreMemberList.Add(memberExpression.Member.Name);
        }

        public TN MapTo(T src, TN dest)
        {
            if (ResultMapFunction == null)
            {
                Compile();
            }
            return ResultMapFunction(src, dest);
        }

        public void MapFunction<TMember, TNMember>(Expression<Func<TN, TNMember>> left, Func<T, TMember> right)
        {
            var memberExpression = left.Body as MemberExpression;
            Expression<Func<T, TMember>> expr = (t) => right(t);

            var parameterExpression = Expression.Parameter(typeof(T));
            var rightExpression = Expression.Invoke(expr, parameterExpression);
            if (typeof(TNMember) != typeof(TMember))
            {
                var mapComplexResult = MappingService.GetDifferentTypeMemberMappingExpression(rightExpression, left.Body as MemberExpression);
                CustomPropertyCache[memberExpression.Member.Name] = mapComplexResult;
            }
            else
            {
                var binaryExpression = Expression.Assign(memberExpression, rightExpression);
                CustomPropertyCache.Add(memberExpression.Member.Name, binaryExpression);
            }
        }
    }
}
