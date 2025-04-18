// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//
// Description:
//      A pending loaded or unloaded operation, to be run by the MediaContext.
//

using System.Windows;               // DependencyObject
using System.Windows.Threading;     // DispatcherOperationCallback

namespace MS.Internal
{
    internal class LoadedOrUnloadedOperation
    {
        internal LoadedOrUnloadedOperation(
            DispatcherOperationCallback callback,
            DependencyObject target)
        {
            Debug.Assert(callback != null && target != null);
            _callback = callback;
            _target = target;
        }

        internal void DoWork()
        {
            if (!_cancelled)
            {
                _callback(_target);
            }
        }

        internal void Cancel()
        {
            _cancelled = true;
        }

        private DispatcherOperationCallback _callback;
        private DependencyObject _target;
        private bool _cancelled;
    }
}
