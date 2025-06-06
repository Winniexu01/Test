// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Description:  Internal class replicating the functionality of the
//      former VB class of the same name.   It's no longer used internally, but
//      exists solely for compat - in case anyone used private reflection.

namespace MS.Internal.Threading
{
    internal delegate object InternalRealCallDelegate(Delegate method, object args, int numArgs);
    internal delegate bool FilterExceptionDelegate(object source, Exception e);
    internal delegate bool CatchExceptionDelegate(object source, Exception e, Delegate catchHandler);

    /// <summary>
    /// Class for Filtering and Catching Exceptions
    /// </summary>
    internal sealed class ExceptionFilterHelper
    {
        internal ExceptionFilterHelper( InternalRealCallDelegate internalRealCall,
                                        FilterExceptionDelegate filterException,
                                        CatchExceptionDelegate catchException)
        {
            _internalRealCall = internalRealCall;
            _filterException = filterException;
            _catchException = catchException;
        }

        internal object TryCatchWhen(   object source,
                                        Delegate method,
                                        object args,
                                        int numArgs,
                                        Delegate catchHandler)
        {
            object result = null;

            try
            {
                result = _internalRealCall.Invoke(method, args, numArgs);
            }
            catch (Exception e) when (_filterException.Invoke(source, e))
            {
                if (!_catchException.Invoke(source, e, catchHandler))
                {
                    throw;
                }
            }

            return result;
        }

        private InternalRealCallDelegate _internalRealCall;
        private FilterExceptionDelegate _filterException;
        private CatchExceptionDelegate _catchException;
    }
}
