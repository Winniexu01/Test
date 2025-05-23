﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MS.Internal;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Reflection;
using System.Windows.Media.Animation;

namespace System.Windows
{
    /// <summary>
    /// PointConverter - Converter class for converting instances of other types to Point instances
    /// </summary>
    /// <ExternalAPI/> 
    public class KeySplineConverter : TypeConverter
    {
        /// <summary>
        /// CanConvertFrom - Returns whether or not this class can convert from a given type
        /// </summary>
        /// <ExternalAPI/>
        public override bool CanConvertFrom(ITypeDescriptorContext typeDescriptor, Type destinationType)
        {
            if (destinationType == typeof(string))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// TypeConverter method override.
        /// </summary>
        /// <param name="context">ITypeDescriptorContext</param>
        /// <param name="destinationType">Type to convert to</param>
        /// <returns>true if conversion is possible</returns>
        /// <ExternalAPI/>
        public override bool CanConvertTo(ITypeDescriptorContext context, Type destinationType)
        {
            if (   destinationType == typeof(InstanceDescriptor)
                || destinationType == typeof(string))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// ConvertFrom
        /// </summary>
        public override object ConvertFrom(
            ITypeDescriptorContext context, 
            CultureInfo cultureInfo, 
            object value)
        {
            string stringValue = value as string;

            if (value == null)
            {
                throw new NotSupportedException(SR.Converter_ConvertFromNotSupported);
            }

            TokenizerHelper th = new TokenizerHelper(stringValue, cultureInfo);

            return new KeySpline(
                Convert.ToDouble(th.NextTokenRequired(), cultureInfo),
                Convert.ToDouble(th.NextTokenRequired(), cultureInfo),
                Convert.ToDouble(th.NextTokenRequired(), cultureInfo),
                Convert.ToDouble(th.NextTokenRequired(), cultureInfo));
        }

        /// <summary>
        /// TypeConverter method implementation.
        /// </summary>
        /// <param name="context">ITypeDescriptorContext</param>
        /// <param name="cultureInfo">current culture (see CLR specs), null is a valid value</param>
        /// <param name="value">value to convert from</param>
        /// <param name="destinationType">Type to convert to</param>
        /// <returns>converted value</returns>
        /// <ExternalAPI/>
        public override object ConvertTo(
            ITypeDescriptorContext context, 
            CultureInfo cultureInfo,
            object value, 
            Type destinationType)
        {
            KeySpline keySpline = value as KeySpline;

            if (keySpline != null && destinationType != null)
            {
                if (destinationType == typeof(InstanceDescriptor))
                {
                    ConstructorInfo ci = typeof(KeySpline).GetConstructor(new Type[] 
                        {
                            typeof(double), typeof(double),
                            typeof(double), typeof(double) 
                        });

                    return new InstanceDescriptor(ci, new object[]
                        {
                            keySpline.ControlPoint1.X, keySpline.ControlPoint1.Y,
                            keySpline.ControlPoint2.X, keySpline.ControlPoint2.Y
                        });
                }
                else if (destinationType == typeof(string))
                {
                    return String.Format(
                        cultureInfo,
                        "{0}{4}{1}{4}{2}{4}{3}",
                        keySpline.ControlPoint1.X,
                        keySpline.ControlPoint1.Y,
                        keySpline.ControlPoint2.X,
                        keySpline.ControlPoint2.Y,
                        cultureInfo != null ? cultureInfo.TextInfo.ListSeparator : CultureInfo.InvariantCulture.TextInfo.ListSeparator);
                }
            }

            // Pass unhandled cases to base class (which will throw exceptions for null value or destinationType.)
            return base.ConvertTo(context, cultureInfo, value, destinationType);
        }
    }
}
