// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//
//
//  Contents:  Implementation of text linebreak control 
//
//  Spec:      Text Formatting API.doc
//
//

namespace System.Windows.Media.TextFormatting
{
    /// <summary>
    /// Specialized text run used to mark the end of a line
    /// </summary>
    public class TextEndOfLine : TextRun
    {
        private int                 _length;
        private TextRunProperties   _textRunProperties;

        #region Constructors

        /// <summary>
        /// Construct a linebreak run
        /// </summary>
        /// <param name="length">number of characters</param>
        public TextEndOfLine(int length) : this(length, null)
        {}


        /// <summary>
        /// Construct a linebreak run
        /// </summary>
        /// <param name="length">number of characters</param>
        /// <param name="textRunProperties">linebreak text run properties</param>
        public TextEndOfLine(
            int                 length, 
            TextRunProperties   textRunProperties
            )
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

            if (textRunProperties != null && textRunProperties.Typeface == null)
                throw new ArgumentNullException("textRunProperties.Typeface");

            _length = length;
            _textRunProperties = textRunProperties;
        }

        #endregion


        /// <summary>
        /// Reference to character buffer
        /// </summary>
        public sealed override CharacterBufferReference CharacterBufferReference
        {
            get { return new CharacterBufferReference(); }
        }

        
        /// <summary>
        /// Character length
        /// </summary>
        public sealed override int Length
        {
            get { return _length; }
        }


        /// <summary>
        /// A set of properties shared by every characters in the run
        /// </summary>
        public sealed override TextRunProperties Properties
        {
            get { return _textRunProperties; }
        }
    }
}

