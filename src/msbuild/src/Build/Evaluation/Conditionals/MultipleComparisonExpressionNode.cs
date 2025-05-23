﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Shared;

#nullable disable

namespace Microsoft.Build.Evaluation
{
    /// <summary>
    /// Evaluates as boolean and evaluates children as boolean, numeric, or string.
    /// Order in which comparisons are attempted is numeric, boolean, then string.
    /// Updates conditioned properties table.
    /// </summary>
    internal abstract class MultipleComparisonNode : OperatorExpressionNode
    {
        private bool _conditionedPropertiesUpdated = false;

        /// <summary>
        /// Compare numbers
        /// </summary>
        protected abstract bool Compare(double left, double right);

        /// <summary>
        /// Compare booleans
        /// </summary>
        protected abstract bool Compare(bool left, bool right);

        /// <summary>
        /// Compare strings
        /// </summary>
        protected abstract bool Compare(string left, string right);

        /// <summary>
        /// Evaluates as boolean and evaluates children as boolean, numeric, or string.
        /// Order in which comparisons are attempted is numeric, boolean, then string.
        /// Updates conditioned properties table.
        /// </summary>
        internal override bool BoolEvaluate(ConditionEvaluator.IConditionEvaluationState state)
        {
            ProjectErrorUtilities.VerifyThrowInvalidProject(
                LeftChild != null && RightChild != null,
                 state.ElementLocation,
                 "IllFormedCondition",
                 state.Condition);

            // It's sometimes possible to bail out of expansion early if we just need to know whether
            // the result is empty string.
            // If at least one of the left or the right hand side will evaluate to empty,
            // and we know which do, then we already have enough information to evaluate this expression.
            // That means we don't have to fully expand a condition like " '@(X)' == '' "
            // which is a performance advantage if @(X) is a huge item list.

            // This is the possible case of an expression similar to '$(a)' == '', where a usage of uninitialized
            //  property is reasonable and should not be flagged by uninitialized reads detection.
            // So if at least one side is empty, we know to signal to PropertiesUseTracker to not flag in this scope.
            // The other side might not be property at all - that's fine, as then PropertiesUseTracker won't be even called.
            if (LeftChild.IsUnexpandedValueEmpty() || RightChild.IsUnexpandedValueEmpty())
            {
                state.PropertiesUseTracker.PropertyReadContext = PropertyReadContext.ConditionEvaluationWithOneSideEmpty;
            }

            bool leftEmpty = LeftChild.EvaluatesToEmpty(state);
            bool rightEmpty = RightChild.EvaluatesToEmpty(state);
            if (leftEmpty || rightEmpty)
            {
                UpdateConditionedProperties(state);

                return Compare(leftEmpty, rightEmpty);
            }
            else if (LeftChild.TryNumericEvaluate(state, out double leftNumericValue) && RightChild.TryNumericEvaluate(state, out double rightNumericValue))
            {
                // The left child evaluating to a number and the right child not evaluating to a number
                // is insufficient to say they are not equal because $(MSBuildToolsVersion) evaluates to
                // the string "Current" most of the time but when doing numeric comparisons, is treated
                // as a version and returns "17.0" (or whatever the current tools version is). This means
                // that if '$(MSBuildToolsVersion)' is "equal" to BOTH '17.0' and 'Current' (if 'Current'
                // is 17.0).
                return Compare(leftNumericValue, rightNumericValue);
            }
            else if (LeftChild.TryBoolEvaluate(state, out bool leftBoolValue) && RightChild.TryBoolEvaluate(state, out bool rightBoolValue))
            {
                return Compare(leftBoolValue, rightBoolValue);
            }

            string leftExpandedValue = LeftChild.GetExpandedValue(state);
            string rightExpandedValue = RightChild.GetExpandedValue(state);

            ProjectErrorUtilities.VerifyThrowInvalidProject(
                leftExpandedValue != null && rightExpandedValue != null,
                    state.ElementLocation,
                    "IllFormedCondition",
                    state.Condition);

            UpdateConditionedProperties(state);

            // reset back the property read context (it's no longer a condition with one side empty)
            state.PropertiesUseTracker.PropertyReadContext = PropertyReadContext.ConditionEvaluation;

            return Compare(leftExpandedValue, rightExpandedValue);
        }

        /// <summary>
        /// Reset temporary state
        /// </summary>
        internal override void ResetState()
        {
            base.ResetState();
            _conditionedPropertiesUpdated = false;
        }

        /// <summary>
        /// Updates the conditioned properties table if it hasn't already been done.
        /// </summary>
        private void UpdateConditionedProperties(ConditionEvaluator.IConditionEvaluationState state)
        {
            if (!_conditionedPropertiesUpdated && state.ConditionedPropertiesInProject != null)
            {
                string leftUnexpandedValue = LeftChild.GetUnexpandedValue(state);
                string rightUnexpandedValue = RightChild.GetUnexpandedValue(state);

                if (leftUnexpandedValue != null)
                {
                    ConditionEvaluator.UpdateConditionedPropertiesTable(
                        state.ConditionedPropertiesInProject,
                         leftUnexpandedValue,
                         RightChild.GetExpandedValue(state));
                }

                if (rightUnexpandedValue != null)
                {
                    ConditionEvaluator.UpdateConditionedPropertiesTable(
                        state.ConditionedPropertiesInProject,
                         rightUnexpandedValue,
                         LeftChild.GetExpandedValue(state));
                }

                _conditionedPropertiesUpdated = true;
            }
        }
    }
}
