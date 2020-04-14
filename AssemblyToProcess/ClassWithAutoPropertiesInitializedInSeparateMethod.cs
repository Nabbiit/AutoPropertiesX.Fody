using AutoProperties;
// ReSharper disable CheckNamespace
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider declaring as nullable.

public class ClassWithAutoPropertiesInitializedInSeparateMethod : ObservableObject
{
    public ClassWithAutoPropertiesInitializedInSeparateMethod()
    {
        Init();
    }

    private void Init()
    {
        Property1 = "Test";
        Property2 = "Test2";
    }

    public string Property1 { get; set; }

    public string Property2 { get; set; }

    public bool IsChanged { get; set; }
}

[BypassAutoPropertySettersInConstructors(true)]
public class ClassWithAutoPropertiesInitializedInSeparateMethodAndBypassAutoPropertySetters : ObservableObject
{
    public ClassWithAutoPropertiesInitializedInSeparateMethodAndBypassAutoPropertySetters()
    {
        Init();
    }

    private void Init()
    {
        Property1 = "Test";
        Property2 = "Test2";
    }

    public string Property1 { get; set; }

    public string Property2 { get; set; }

    public bool IsChanged { get; set; }
}