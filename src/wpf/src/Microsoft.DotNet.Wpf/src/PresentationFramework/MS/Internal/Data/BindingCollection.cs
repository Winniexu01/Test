// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
//
// Description: Defines BindingList object, a list of binds.
//
// Specs:       UIBind.mht
//

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;

namespace MS.Internal.Data
{
    /// <summary>
    ///  A list of bindingss, used by MultiBinding classes.
    /// </summary>
    internal class BindingCollection : Collection<BindingBase>
    {
        //------------------------------------------------------
        //
        //  Constructors
        //
        //------------------------------------------------------

        /// <summary> Constructor </summary>
        internal BindingCollection(BindingBase owner, BindingCollectionChangedCallback callback)
        {
            Invariant.Assert(owner != null && callback != null);
            _owner = owner;
            _collectionChangedCallback = callback;
        }

        // disable default constructor
        private BindingCollection()
        {
        }



        //------------------------------------------------------
        //
        //  Protected Methods
        //
        //------------------------------------------------------

        #region Protected Methods

        /// <summary>
        /// called by base class Collection&lt;T&gt; when the list is being cleared;
        /// raises a CollectionChanged event to any listeners
        /// </summary>
        protected override void ClearItems()
        {
            _owner.CheckSealed();
            base.ClearItems();
            OnBindingCollectionChanged();
        }

        /// <summary>
        /// called by base class Collection&lt;T&gt; when an item is removed from list;
        /// raises a CollectionChanged event to any listeners
        /// </summary>
        protected override void RemoveItem(int index)
        {
            _owner.CheckSealed();
            base.RemoveItem(index);
            OnBindingCollectionChanged();
        }

        /// <summary>
        /// called by base class Collection&lt;T&gt; when an item is added to list;
        /// raises a CollectionChanged event to any listeners
        /// </summary>
        protected override void InsertItem(int index, BindingBase item)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateItem(item);
            _owner.CheckSealed();

            base.InsertItem(index, item);
            OnBindingCollectionChanged();
        }

        /// <summary>
        /// called by base class Collection&lt;T&gt; when an item is added to list;
        /// raises a CollectionChanged event to any listeners
        /// </summary>
        protected override void SetItem(int index, BindingBase item)
        {
            ArgumentNullException.ThrowIfNull(item);
            ValidateItem(item);
            _owner.CheckSealed();

            base.SetItem(index, item);
            OnBindingCollectionChanged();
        }

        #endregion Protected Methods


        //------------------------------------------------------
        //
        //  Private Methods
        //
        //------------------------------------------------------

        private void ValidateItem(BindingBase binding)
        {
            // for V1, we only allow Binding as an item of BindingCollection.
            if (!(binding is Binding))
                throw new NotSupportedException(SR.Format(SR.BindingCollectionContainsNonBinding, binding.GetType().Name));
        }

        private void OnBindingCollectionChanged()
        {
            if (_collectionChangedCallback != null)
                _collectionChangedCallback();
        }

        //------------------------------------------------------
        //
        //  Private Fields
        //
        //------------------------------------------------------

        private BindingBase _owner;
        private BindingCollectionChangedCallback _collectionChangedCallback;
    }

    // the delegate to use for getting BindingListChanged notifications
    internal delegate void BindingCollectionChangedCallback();
}
