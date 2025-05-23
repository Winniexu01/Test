// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Serialization;

namespace System.Windows.Xps
{
    /// <summary>
    /// This class is the base class for all exceptions that are
    /// thrown by the Xps packaging and serialization APIs.
    /// </summary>
    [Serializable]
    public class XpsException : Exception
    {
        #region Constructors

        /// <summary>
        /// 
        /// </summary>
        public
        XpsException(
            )
            : base()
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        public
        XpsException(
            string              message
            )
            : base(message)
        {
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="message"></param>
        /// <param name="innerException"></param>
        public
        XpsException(
            string              message,
            Exception           innerException
            )
            : base(message, innerException)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="info"></param>
        /// <param name="context"></param>
#pragma warning disable SYSLIB0051 // Type or member is obsolete
        protected
        XpsException(
            SerializationInfo   info,
            StreamingContext    context
            )
            : base(info, context)
        {
        }
#pragma warning restore SYSLIB0051 // Type or member is obsolete
        #endregion Constructors
    }
}