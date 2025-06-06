﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.



#region Using declarations

using System.Windows.Automation.Provider;
#if RIBBON_IN_FRAMEWORK
using System.Windows.Controls.Ribbon;

#if RIBBON_IN_FRAMEWORK
namespace System.Windows.Automation.Peers
#else
namespace Microsoft.Windows.Automation.Peers
#endif
{
#else
    using Microsoft.Windows.Controls.Ribbon;
    using System.Windows;
    using System.Windows.Automation.Peers;
    using System.Windows.Controls;
#endif

    #endregion

    public class RibbonGalleryItemDataAutomationPeer : ItemAutomationPeer, IScrollItemProvider, ISelectionItemProvider
    {
        #region constructor

        ///
        public RibbonGalleryItemDataAutomationPeer(object owner, ItemsControlAutomationPeer itemsControlAutomationPeer, RibbonGalleryCategoryDataAutomationPeer parentCategoryDataAutomationPeer)
            : base(owner, itemsControlAutomationPeer)
        {
            _parentCategoryDataAutomationPeer = parentCategoryDataAutomationPeer;
        }

        #endregion constructor

        #region AutomationPeer override

        ///
        protected override string GetClassNameCore()
        {
            return "RibbonGalleryItem";
        }

        ///
        protected override AutomationControlType GetAutomationControlTypeCore()
        {
            return AutomationControlType.ListItem;
        }

        ///
        public override object GetPattern(PatternInterface patternInterface)
        {
            if (patternInterface == PatternInterface.ScrollItem || patternInterface == PatternInterface.SelectionItem)
            {
                return this;
            }
            return null;
        }

        #endregion AutomationPeer override

        #region public properties

        public RibbonGalleryCategoryDataAutomationPeer ParentCategoryDataAutomationPeer
        {
            get
            {
                return _parentCategoryDataAutomationPeer;
            }
        }

        #endregion public properties

        #region IScrollItemProvider Members

        /// <summary>
        /// call wrapper.BringIntoView
        /// </summary>
        void IScrollItemProvider.ScrollIntoView()
        {
            RibbonGalleryItem ribbonGalleryItem = GetWrapper() as RibbonGalleryItem;
            ribbonGalleryItem?.BringIntoView();
        }

        #endregion

        #region ISelectionItemProvider Members

        void ISelectionItemProvider.AddToSelection()
        {
            throw new InvalidOperationException();
        }

        bool ISelectionItemProvider.IsSelected
        {
            get 
            {
                RibbonGalleryItem ribbonGalleryItem = GetWrapper() as RibbonGalleryItem;
                if (ribbonGalleryItem != null)
                {
                    return ribbonGalleryItem.IsSelected;
                }
                else
                {
                    throw new ElementNotAvailableException();
                }
            }
        }

        void ISelectionItemProvider.RemoveFromSelection()
        {
            RibbonGalleryItem ribbonGalleryItem = GetWrapper() as RibbonGalleryItem;
            if (ribbonGalleryItem != null)
            {
                ribbonGalleryItem.IsSelected = false;
            }
            else
                throw new ElementNotAvailableException();
        }

        void ISelectionItemProvider.Select()
        {
            RibbonGalleryItem ribbonGalleryItem = GetWrapper() as RibbonGalleryItem;
            if (ribbonGalleryItem != null)
            {
                ribbonGalleryItem.IsSelected = true;
            }
            else
                throw new ElementNotAvailableException();
        }

        IRawElementProviderSimple ISelectionItemProvider.SelectionContainer
        {
            get
            {
                RibbonGalleryCategoryDataAutomationPeer categoryDataPeer = ParentCategoryDataAutomationPeer;
                if(categoryDataPeer != null)
                {
                    RibbonGalleryAutomationPeer galleryAutomationPeer = categoryDataPeer.GetParent() as RibbonGalleryAutomationPeer;
                    if (galleryAutomationPeer != null)
                        return ProviderFromPeer(galleryAutomationPeer);
                }

                return null;
            }
        }

        #endregion

        #region data

        private RibbonGalleryCategoryDataAutomationPeer _parentCategoryDataAutomationPeer;

        #endregion

#if !RIBBON_IN_FRAMEWORK
        #region Private methods

        private UIElement GetWrapper()
        {
            UIElement wrapper = null;
            ItemsControlAutomationPeer itemsControlAutomationPeer = ItemsControlAutomationPeer;
            if (itemsControlAutomationPeer != null)
            {
                ItemsControl owner = (ItemsControl)(itemsControlAutomationPeer.Owner);
                if (owner != null)
                {
                    wrapper = owner.ItemContainerGenerator.ContainerFromItem(Item) as UIElement;
                }
            }
            return wrapper;
        }

        private AutomationPeer GetWrapperPeer()
        {
            AutomationPeer wrapperPeer = null;
            UIElement wrapper = GetWrapper();
            if (wrapper != null)
            {
                wrapperPeer = UIElementAutomationPeer.CreatePeerForElement(wrapper);
                if (wrapperPeer == null)
                {
                    if (wrapper is FrameworkElement)
                        wrapperPeer = new FrameworkElementAutomationPeer((FrameworkElement)wrapper);
                    else
                        wrapperPeer = new UIElementAutomationPeer(wrapper);
                }
            }

            return wrapperPeer;
        }

        #endregion
#endif

    }
}
