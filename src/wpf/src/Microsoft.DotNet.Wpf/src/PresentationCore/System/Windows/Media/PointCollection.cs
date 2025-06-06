// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// 
//
// Description: Point collection partial class. 
//
//

using System.IO;
using MS.Internal.Media;

namespace System.Windows.Media
{
    public partial class PointCollection
    {
        ///<summary>
        /// Deserialize this object from BAML
        ///</summary>
        internal static object DeserializeFrom(BinaryReader reader)
        {
            // Get the size.
            uint count = reader.ReadUInt32() ; 
            
            PointCollection collection = new PointCollection( (int) count) ; 
            
            for ( uint i = 0; i < count ; i ++ ) 
            {
                Point point = new Point(
                                             XamlSerializationHelper.ReadDouble( reader ), 
                                             XamlSerializationHelper.ReadDouble( reader ) ) ; 

                collection.Add( point );                 
            }

            return collection ; 
        }
     
        
    }
}
