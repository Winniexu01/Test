﻿// <auto-generated/>
#pragma warning disable 1591
namespace Test
{
    #line default
    using global::System;
    using global::System.Collections.Generic;
    using global::System.Linq;
    using global::System.Threading.Tasks;
    using global::Microsoft.AspNetCore.Components;
#nullable restore
#line (1,2)-(1,43) "x:\dir\subdir\Test\TestComponent.cshtml"
using Microsoft.AspNetCore.Components.Web

#nullable disable
    ;
    #line default
    #line hidden
    #nullable restore
    public partial class TestComponent : global::Microsoft.AspNetCore.Components.ComponentBase
    #nullable disable
    {
        #pragma warning disable 1998
        protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
        {
            __builder.OpenComponent<global::Test.TestComponent>(0);
            __builder.AddComponentParameter(1, "method", "post");
            __builder.AddComponentParameter(2, "onsubmit", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::System.EventArgs>(this, 
#nullable restore
#line (2,41)-(2,50) "x:\dir\subdir\Test\TestComponent.cshtml"
() => { }

#line default
#line hidden
#nullable disable
            ));
            __builder.AddComponentParameter(3, "@formname", "named-form-handler");
            __builder.CloseComponent();
            __builder.AddMarkupContent(4, "\r\n");
            __builder.OpenComponent<global::Test.TestComponent>(5);
            __builder.AddComponentParameter(6, "method", "post");
            __builder.AddComponentParameter(7, "onsubmit", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::System.EventArgs>(this, 
#nullable restore
#line (3,41)-(3,50) "x:\dir\subdir\Test\TestComponent.cshtml"
() => { }

#line default
#line hidden
#nullable disable
            ));
            __builder.AddComponentParameter(8, "@formname", 
#nullable restore
#line (3,65)-(3,85) "x:\dir\subdir\Test\TestComponent.cshtml"
"named-form-handler"

#line default
#line hidden
#nullable disable
            );
            __builder.CloseComponent();
        }
        #pragma warning restore 1998
    }
}
#pragma warning restore 1591
