// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Windows.Xps.Serialization.RCW
{
    [Guid("0C733A30-2A1C-11CE-ADE5-00AA0044773D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComImport]
    internal interface ISequentialStream
    {
        [MethodImpl(MethodImplOptions.InternalCall)]
        void RemoteRead(out byte pv, [In] uint cb, out uint pcbRead);

        [MethodImpl(MethodImplOptions.InternalCall)]
        void RemoteWrite([In] ref byte pv, [In] uint cb, out uint pcbWritten);
    }
}
