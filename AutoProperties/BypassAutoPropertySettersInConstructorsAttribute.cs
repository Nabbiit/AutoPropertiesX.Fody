using System;

#pragma warning disable CS9113 // 参数未读。

namespace AutoProperties
{
    /// <summary>
    /// Controls whether property setters for auto-properties are bypassed when a value is assigned to an auto-property in a constructor.<para/>
    /// When this attribute is set with it's parameter being <c>true</c>, all property setters of auto-properties are replaced with code that assigns the value directly to the backing field.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="BypassAutoPropertySettersInConstructorsAttribute" /> class.
    /// </remarks>
    /// <param name="value">
    /// if set to <c>true</c>, assigning values to auto-properties in the constructors be replaced by code that assigns the value directly to the backing field;
    /// otherwise, the code is not changed.
    /// </param>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Assembly)]
    public sealed class BypassAutoPropertySettersInConstructorsAttribute(bool value) : Attribute;
}
