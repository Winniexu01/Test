﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Windows;
using MS.Win32;

namespace MS.Internal.Controls
{
    internal class ConnectionPointCookie
    {
        private UnsafeNativeMethods.IConnectionPoint connectionPoint;
        private int cookie;

        
        /// Creates a connection point to of the given interface type.
        /// which will call on a managed code sink that implements that interface.
        internal ConnectionPointCookie(object source, object sink, Type eventInterface)
        {
            Exception ex = null;
            if (source is UnsafeNativeMethods.IConnectionPointContainer cpc)
            {
                try
                {
                    Guid tmp = eventInterface.GUID;
                    if (cpc.FindConnectionPoint(ref tmp, out connectionPoint) != NativeMethods.S_OK)
                    {
                        connectionPoint = null;
                    }
                }
                catch (Exception e)
                {
                    if (CriticalExceptions.IsCriticalException(e))
                    {
                        throw;
                    }

                    connectionPoint = null;
                }

                if (connectionPoint == null)
                {
                    ex = new ArgumentException(SR.Format(SR.AxNoEventInterface, eventInterface.Name));
                }
                // IsComObject(sink): this is the case of a managed sink object wrapped in IDispatchSTAForwarder -
                // see WebBrowser.CreateSink().
                else if (sink == null || !eventInterface.IsInstanceOfType(sink) && !Marshal.IsComObject(sink))
                {
                    ex = new InvalidCastException(SR.Format(SR.AxNoSinkImplementation, eventInterface.Name));
                }
                else
                {
                    int hr = connectionPoint.Advise(sink, ref cookie);
                    if (hr != NativeMethods.S_OK)
                    {
                        cookie = 0;
                        Marshal.FinalReleaseComObject(connectionPoint);
                        connectionPoint = null;
                        ex = new InvalidOperationException(SR.Format(SR.AxNoSinkAdvise, eventInterface.Name, hr));
                    }
                }
            }
            else
            {
                ex = new InvalidCastException(SR.AxNoConnectionPointContainer);
            }


            if (connectionPoint == null || cookie == 0)
            {
                if (connectionPoint != null)
                {
                    Marshal.FinalReleaseComObject(connectionPoint);
                }

                if (ex == null)
                {
                    throw new ArgumentException(SR.Format(SR.AxNoConnectionPoint, eventInterface.Name));
                }
                else
                {
                    throw ex;
                }
            }
        }

        /// <summary>
        /// Disconnect the current connection point.  If the object is not connected,
        /// this method will do nothing.
        /// </summary>
        internal void Disconnect()
        {
            if (connectionPoint != null && cookie != 0)
            {
                try
                {
                    connectionPoint.Unadvise(cookie);
                }
                catch (Exception ex)
                {
                    if (CriticalExceptions.IsCriticalException(ex))
                    {
                        throw;
                    }
                }
                finally
                {
                    cookie = 0;
                }


                try
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(connectionPoint);
                }
                catch (Exception ex)
                {
                    if (CriticalExceptions.IsCriticalException(ex))
                    {
                        throw;
                    }
                }
                finally
                {
                    connectionPoint = null;
                }
            }
        }

        ~ConnectionPointCookie()
        {
            Disconnect();
        }
    }
}
