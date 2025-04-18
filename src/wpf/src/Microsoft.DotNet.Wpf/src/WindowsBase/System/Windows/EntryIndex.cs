// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

/***************************************************************************\
*
*
*  This file describes an index that refers to an EffectiveValueEntry.
*  Found is used to indicate whether or not the index is currently populated
*  with the appropriate DP or not.
*
*
\***************************************************************************/


namespace System.Windows
{
    internal struct EntryIndex
    {
        public EntryIndex(uint index)
        {
            // Found is true
            _store = index | 0x80000000;
        }
        
        public EntryIndex(uint index, bool found)
        {
            _store = index & 0x7FFFFFFF;
            if (found)
            {
                _store |= 0x80000000;
            }
        }

        public bool Found
        {
            get { return (_store & 0x80000000) != 0; }
        }

        public uint Index
        {
            get { return _store & 0x7FFFFFFF; }
        }

        private uint _store;
    }   
}

