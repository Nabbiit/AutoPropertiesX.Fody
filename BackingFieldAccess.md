### This is an add-in for [Fody](https://github.com/Fody/Fody/) ![badge](https://tom-englert.visualstudio.com/_apis/public/build/definitions/75bf84d2-d359-404a-a712-07c9f693f635/12/badge) [![NuGet Status](http://img.shields.io/nuget/v/AutoProperties.Fody.svg?style=flat-square)](https://www.nuget.org/packages/AutoProperties.Fody)
![Icon](package_icon.png)

### Background

Usually there is no need to access the backing field of an auto-property, because the property setter does just write to the backing field - no more an no less.<para/>

However the story is different when some [Fody](https://github.com/Fody/Fody/) plug-in has extended the setter of auto-properties, like e.g. [Fody.PropertyChanged](https://github.com/Fody/PropertyChanged) does.

When assigning a value to the property, the now modified setter calls `OnPropertyChanged`, which is virtual by default.
If you do this from within the constructor, the constructor has not yet finished, any event handlers assigned in the constructor or code in the overwritten `OnPropertyChanged` method will work on an only partial initialized object, 
which can easily lead to crashes if the event handler is not aware of this. This is why you get e.g. the [CA2214](https://docs.microsoft.com/en-us/visualstudio/code-quality/ca2214-do-not-call-overridable-methods-in-constructors) warning.

With old-style properties you can just bypass this problem by initializing the backing field instead of the property, but with auto-properties you have no chance to do so.

With this Fody add-in you can control if the property setter for auto-properties should be bypassed, and replaced by code that sets just the backing field.

#### Sample
This sample illustrates the problem: Even though there is a null guard in the constructor of the derived class, calling `new Derived(new List<string>())` will 
crash in `OnPropertyChanged` with a `NullReferenceException`:
```C#
public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        Property1 = property1;
        Property2 = property2;
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}

public class Derived : Class
{
    private readonly IList<string> _changes;

    public Derived(IList<string> changes)
        : base("Test1", "Test2")
    {
        if (changes == null)
            throw new ArgumentNullException(nameof(changes));

        _changes = changes;
    }

    protected override void OnPropertyChanged(string propertyName)
    {
        _changes.Add(propertyName);

        base.OnPropertyChanged(propertyName);
    }
}
```

### Using the `SetBackingField` extension method
Using the `SetBackingField` extension method you can control backing field access per property. 
This is the recommended usage: It is very explicit and visible, so it should be clear to everyone what happens.
It will fix [CA2214](https://docs.microsoft.com/en-us/visualstudio/code-quality/ca2214-do-not-call-overridable-methods-in-constructors) for the 
Roslyn source code analyzers as well as for the older FxCop IL-analyzer.

```C#
public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        Property1.SetBackingField(property1);
        Property2.SetBackingField(property2)
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}
```
will become:
```C#
public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        this.<Property1>k__BackingField = property1;
        this.<Property2>k__BackingField = property2;
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}
```

The property setters will be bypassed, and no virtual method will be called from within the constructor. 

**NOTE:** This extension method is only valid on auto-properties of the class itself. 
Trying to use this on something else than an auto-property or on an auto-property of another class - including the base class - will 
generate an error.

### Using the `BypassAutoPropertySettersInConstructors` attribute
With this attribute you can control backing field access on a wider scope. 
You can apply this to individual classes or even to a whole assembly.

All auto-property setters in all constructors in the scope will be replaced with a setter of the backing field.
This is not as obvious as using the `SetBackingField` extension method, so use with special care. 
A use case is e.g. an assembly with lots of POCO classes that should be made observable with minimal code changes.

Since the effect of this will only be visible in the IL-code, the Roslyn source code analyzers will still complain 
about [CA2214](https://docs.microsoft.com/en-us/visualstudio/code-quality/ca2214-do-not-call-overridable-methods-in-constructors).

```C#
[BypassAutoPropertySettersInConstructors(true)]
public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        Property1 = property1;
        Property2 = property2;
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}
```
will become:
```C#
public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        this.<Property1>k__BackingField = property1;
        this.<Property2>k__BackingField = property2;
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}
```
NOTE: You can opt in on assembly level using `[assembly: BypassAutoPropertySettersInConstructors(false)]`, and then opt out again for individual classes using `[BypassAutoPropertySettersInConstructors(false)]`

### Opting out again with the `SetProperty` extension method
This extension method can be useful if you have opted in on assembly level with the `BypassAutoPropertySettersInConstructors` attribute, but 
need opt out again for a special property:
```C#
[assembly:BypassAutoPropertySettersInConstructors(true)]


public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        Property1 = property1;
        Property2.SetProperty(property2);
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}
```
will become:
```C#
public class Class : ObservableObject
{
    public Class(string property1, string property2)
    {
        this.<Property1>k__BackingField = property1;
        Property2 = property2;
    }

    public string Property1 { get; set; }
    public string Property2 { get; set; }
}
```
**NOTE:** This extension method is only valid on auto-properties of the class itself. 
Trying to use this on something else than an auto-property or on an auto-property of another class - including the base class - will 
generate an error.

