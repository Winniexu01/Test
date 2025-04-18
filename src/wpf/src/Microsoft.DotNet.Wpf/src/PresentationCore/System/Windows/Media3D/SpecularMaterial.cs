// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//
//
// Description: 3D specular material
//
//              See spec at *** FILL IN LATER ***
//

namespace System.Windows.Media.Media3D
{
    /// <summary>
    ///     SpecularMaterial allows a 2d brush to be used on a 3d model that has been lit
    ///     with a specular lighting model
    /// </summary>
    public sealed partial class SpecularMaterial : Material
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        #region Constructors

        /// <summary>
        ///     Constructs a SpecularMaterial
        /// </summary>
        public SpecularMaterial()
        {
        }

        /// <summary>
        ///     Constructor that sets the initial values
        /// </summary>
        /// <param name="brush">The new material's brush</param>
        /// <param name="specularPower">The specular exponent.</param>
        public SpecularMaterial(Brush brush, double specularPower)
        {
            Brush = brush;
            SpecularPower = specularPower;
        }

        #endregion Constructors
    }
}
