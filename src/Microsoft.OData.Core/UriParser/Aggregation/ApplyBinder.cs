﻿//---------------------------------------------------------------------
// <copyright file="ApplyBinder.cs" company="Microsoft">
//      Copyright (C) Microsoft Corporation. All rights reserved. See License.txt in the project root for license information.
// </copyright>
//---------------------------------------------------------------------

namespace Microsoft.OData.Core.UriParser.Aggregation
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using Microsoft.OData.Edm;
    using Microsoft.OData.Edm.Library;
    using Microsoft.OData.Core.UriParser.Parsers;
    using Microsoft.OData.Core.UriParser.Semantic;
    using Microsoft.OData.Core.UriParser.Syntactic;
    using Microsoft.OData.Core.UriParser.TreeNodeKinds;
    using ODataErrorStrings = Microsoft.OData.Core.Strings;

    internal sealed class ApplyBinder
    {
        private MetadataBinder.QueryTokenVisitor bindMethod;

        private BindingState state;

        private FilterBinder filterBinder;

        private IEnumerable<AggregateExpression> aggregateExpressionsCache;

        public ApplyBinder(MetadataBinder.QueryTokenVisitor bindMethod, BindingState state)
        {
            this.bindMethod = bindMethod;
            this.state = state;
            this.filterBinder = new FilterBinder(bindMethod, state);
        }

        public ApplyClause BindApply(IEnumerable<QueryToken> tokens)
        {
            ExceptionUtils.CheckArgumentNotNull(tokens, "tokens");

            var transformations = new List<TransformationNode>();
            foreach (var token in tokens)
            {
                switch (token.Kind)
                {
                    case QueryTokenKind.Aggregate:
                        var aggregate = BindAggregateToken((AggregateToken)(token));
                        transformations.Add(aggregate);
                        aggregateExpressionsCache = aggregate.Expressions;
                        state.AggregatedPropertyNames =
                            aggregate.Expressions.Select(statement => statement.Alias).ToList();
                        break;
                    case QueryTokenKind.AggregateGroupBy:
                        var groupBy = BindGroupByToken((GroupByToken)(token));
                        transformations.Add(groupBy);
                        break;
                    default:
                        var filterClause = this.filterBinder.BindFilter(token);
                        var filterNode = new FilterTransformationNode(filterClause);
                        transformations.Add(filterNode);
                        break;
                }
            }

            return new ApplyClause(transformations);
        }

        private AggregateTransformationNode BindAggregateToken(AggregateToken token)
        {
            var statements = new List<AggregateExpression>();
            foreach (var statementToken in token.Expressions)
            {
                statements.Add(BindAggregateExpressionToken(statementToken));
            }

            return new AggregateTransformationNode(statements);
        }

        private AggregateExpression BindAggregateExpressionToken(AggregateExpressionToken token)
        {
            var expression = this.bindMethod(token.Expression) as SingleValueNode;

            if (expression == null)
            {
                throw new ODataException(ODataErrorStrings.ApplyBinder_AggregateExpressionNotSingleValue(token.Expression));
            }

            var typeReference = CreateAggregateExpressionTypeReference(expression, token.Method);

            // TODO: Determine source
            return new AggregateExpression(expression, token.Method, token.Alias, typeReference);
        }

        private IEdmTypeReference CreateAggregateExpressionTypeReference(SingleValueNode expression, AggregationMethod withVerb)
        {
            var expressionType = expression.TypeReference;
            if (expressionType == null && aggregateExpressionsCache != null)
            {
                var openProperty = expression as SingleValueOpenPropertyAccessNode;
                if (openProperty != null)
                {
                    expressionType = GetTypeReferenceByPropertyName(openProperty.Name);
                }
            }

            switch (withVerb)
            {
                case AggregationMethod.Average:
                    var expressionPrimitiveKind = expressionType.PrimitiveKind();
                    switch (expressionPrimitiveKind)
                    {
                        case EdmPrimitiveTypeKind.Int32:
                        case EdmPrimitiveTypeKind.Int64:
                        case EdmPrimitiveTypeKind.Double:
                            return EdmCoreModel.Instance.GetPrimitive(EdmPrimitiveTypeKind.Double, expressionType.IsNullable);
                        case EdmPrimitiveTypeKind.Decimal:
                            return EdmCoreModel.Instance.GetPrimitive(EdmPrimitiveTypeKind.Decimal, expressionType.IsNullable);
                        default:
                            throw new ODataException(
                                ODataErrorStrings.ApplyBinder_AggregateExpressionIncompatibleTypeForMethod(expression,
                                    expressionPrimitiveKind));
                    }

                case AggregationMethod.CountDistinct:
                    return EdmCoreModel.Instance.GetPrimitive(EdmPrimitiveTypeKind.Int64, false);
                case AggregationMethod.Max:
                case AggregationMethod.Min:
                case AggregationMethod.Sum:
                    return expressionType;
                default:
                    throw new ODataException(ODataErrorStrings.ApplyBinder_UnsupportedAggregateMethod(withVerb));
            }
        }

        private IEdmTypeReference GetTypeReferenceByPropertyName(string name)
        {
            return aggregateExpressionsCache.First(statement => statement.Alias.Equals(name)).TypeReference;
        }

        private GroupByTransformationNode BindGroupByToken(GroupByToken token)
        {
            var properties = new List<GroupByPropertyNode>();

            foreach (var propertyToken in token.Properties)
            {
                var bindResult = this.bindMethod(propertyToken);
                var property = bindResult as SingleValuePropertyAccessNode;

                if (property != null)
                {
                    RegisterProperty(properties, ReversePropertyPath(property));
                }
                else
                {
                    var openProperty = bindResult as SingleValueOpenPropertyAccessNode;
                    if (openProperty != null)
                    {
                        var type = GetTypeReferenceByPropertyName(openProperty.Name);
                        properties.Add(new GroupByPropertyNode(openProperty.Name, openProperty, type));
                    }
                    else
                    {
                        throw new ODataException(
                            ODataErrorStrings.ApplyBinder_GroupByPropertyNotPropertyAccessValue(propertyToken.Identifier));
                    }
                }
            }

            TransformationNode aggregate = null;
            if (token.Child != null)
            {
                if (token.Child.Kind == QueryTokenKind.Aggregate)
                {
                    aggregate = BindAggregateToken((AggregateToken)token.Child);
                    aggregateExpressionsCache = ((AggregateTransformationNode)aggregate).Expressions;
                    state.AggregatedPropertyNames =
                        aggregateExpressionsCache.Select(statement => statement.Alias).ToList();
                }
                else
                {
                    throw new ODataException(ODataErrorStrings.ApplyBinder_UnsupportedGroupByChild(token.Child.Kind));
                }
            }

            // TODO: Determine source
            return new GroupByTransformationNode(properties, aggregate, null);
        }

        private static bool IsPropertyNode(SingleValueNode node)
        {
            return node.Kind == QueryNodeKind.SingleValuePropertyAccess ||
                   node.Kind == QueryNodeKind.SingleNavigationNode;
        }

        private static Stack<SingleValueNode> ReversePropertyPath(SingleValueNode node)
        {
            var result = new Stack<SingleValueNode>();
            do
            {
                result.Push(node);
                if (node.Kind == QueryNodeKind.SingleValuePropertyAccess)
                {
                    node = ((SingleValuePropertyAccessNode)node).Source;
                }
                else if (node.Kind == QueryNodeKind.SingleNavigationNode)
                {
                    node = ((SingleNavigationNode)node).NavigationSource as SingleValueNode;
                }
            } 
            while (node != null && IsPropertyNode(node));

            return result;
        }

        private static void RegisterProperty(IList<GroupByPropertyNode> properties, Stack<SingleValueNode> propertyStack)
        {
            var property = propertyStack.Pop();
            string propertyName = GetNodePropertyName(property);

            if (propertyStack.Count != 0)
            {
                // Not at the leaf, let's add to the container.
                var containerProperty = properties.FirstOrDefault(p => p.Name == propertyName);
                if (containerProperty == null)
                {
                    // We do not have container yet. Create it.
                    containerProperty = new GroupByPropertyNode(propertyName, null);
                    properties.Add(containerProperty);
                }

                RegisterProperty(containerProperty.ChildTransformations, propertyStack);
            }
            else
            {
                // It's the leaf just add.
                var accessNode = property as SingleValuePropertyAccessNode;
                properties.Add(new GroupByPropertyNode(propertyName, property, accessNode.TypeReference));
            }
        }

        private static string GetNodePropertyName(SingleValueNode property)
        {
            if (property.Kind == QueryNodeKind.SingleValuePropertyAccess)
            {
                return ((SingleValuePropertyAccessNode)property).Property.Name;
            }
            else if (property.Kind == QueryNodeKind.SingleNavigationNode)
            {
                return ((SingleNavigationNode)property).NavigationProperty.Name;
            }
            else
            {
                throw new NotSupportedException();
            }
        }
    }
}
