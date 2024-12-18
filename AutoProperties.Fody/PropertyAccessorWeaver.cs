using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using FodyTools;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

using FieldAttributes = Mono.Cecil.FieldAttributes;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using TypeAttributes = Mono.Cecil.TypeAttributes;

namespace AutoProperties.Fody
{
    internal sealed class PropertyAccessorWeaver(ModuleWeaver moduleWeaver, SystemReferences systemReferences)
    {
        private readonly ModuleDefinition _moduleDefinition = moduleWeaver.ModuleDefinition;
        private readonly SystemReferences _systemReferences = systemReferences;

        private readonly ModuleWeaver _moduleWeaver = moduleWeaver;
        private readonly ILogger _logger = moduleWeaver;

        public void Execute()
        {
            try
            {
                var allTypes = _moduleDefinition.GetTypes();
                var allClasses = allTypes.Where(type => (type != null) && type.IsClass && (type.BaseType != null));
                var allInterceptors = new Dictionary<TypeReference, Interceptors>(TypeReferenceEqualityComparer.Default);

                foreach (var classDefinition in allClasses.SelectMany(item => item.GetSelfAndBaseTypes().Reverse()))
                {
                    if (allInterceptors.ContainsKey(classDefinition))
                        continue;

                    try
                    {
                        var baseType = classDefinition.BaseType;

                        if (baseType?.IsGenericInstance == true)
                            baseType = baseType.GetElementType();

                        allInterceptors.Add(classDefinition, new Interceptors(this, classDefinition, allInterceptors.GetValueOrDefault(baseType)));
                    }
                    catch (WeavingException exception)
                    {
                        _logger.LogError(exception.Message, exception.Method);
                    }
                }

                foreach (var interceptors in allInterceptors.Values.Where(item => item.ClassDefinition.Module == _moduleDefinition))
                {
                    try
                    {
                        interceptors.Execute();
                    }
                    catch (WeavingException exception)
                    {
                        _logger.LogError(exception.Message, exception.Method);
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogError($"""
                                   Unhandled exception. Weaving aborted.
                                   {exception}
                                   """);
            }
        }

        private sealed class Interceptors
        {
            private readonly PropertyAccessorWeaver _accessorWeaver;
            private readonly Interceptors? _baseTypeInterceptors;

            private readonly MethodDefinition? _setInterceptor;
            private readonly MethodDefinition? _getInterceptor;

            public TypeDefinition ClassDefinition { get; }

            public MethodDefinition? SetInterceptor => _setInterceptor ?? WhenAccessibleInDerivedClass(_baseTypeInterceptors?.SetInterceptor);

            public MethodDefinition? GetInterceptor => _getInterceptor ?? WhenAccessibleInDerivedClass(_baseTypeInterceptors?.GetInterceptor);

            public Interceptors(PropertyAccessorWeaver accessorWeaver, TypeReference classDefinition, Interceptors? baseTypeInterceptors)
            {
                _accessorWeaver = accessorWeaver;
                _baseTypeInterceptors = baseTypeInterceptors;

                ClassDefinition = classDefinition.Resolve();

                var allMethods = ClassDefinition.Methods;

                if (allMethods is null)
                    return;

                var getInterceptors = allMethods.Where(m => m?.CustomAttributes?.GetAttribute(AttributeNames.GetInterceptor) != null).ToArray();

                if (getInterceptors.Length > 1)
                    throw new WeavingException($"Multiple [GetInterceptor] attributed methods found in class {classDefinition}.", getInterceptors[1]);

                _getInterceptor = getInterceptors.FirstOrDefault();

                var setInterceptors = allMethods.Where(m => m?.CustomAttributes?.GetAttribute(AttributeNames.SetInterceptor) != null).ToArray();

                if (setInterceptors.Length > 1)
                    throw new WeavingException($"Multiple [SetInterceptor] attributed methods found in class {classDefinition}.", setInterceptors[1]);

                _setInterceptor = setInterceptors.FirstOrDefault();

                VerifyInterceptors();
            }

            public void Execute()
            {
                new ClassWeaver(_accessorWeaver, ClassDefinition).Execute(this);
                new ClassFinalizeWeaver(_accessorWeaver, ClassDefinition).Execute(this);
            }

            private void VerifyInterceptors()
            {
                VerifyGetInterceptor();
                VerifySetInterceptor();
            }

            private void VerifySetInterceptor()
            {
                if (_setInterceptor is null)
                    return;

                if (_setInterceptor.ReturnType?.FullName != "System.Void")
                    throw new WeavingException($"The set interceptor of class {ClassDefinition} must not return a value.", _setInterceptor);
            }

            private void VerifyGetInterceptor()
            {
                if (_getInterceptor is null)
                    return;

                var returnType = _getInterceptor.ReturnType;
                var genericParameter = _getInterceptor.GenericParameters?.FirstOrDefault();

                if (genericParameter is not null)
                {
                    if (returnType?.GetElementType() != genericParameter)
                    {
                        throw new WeavingException($"The return type of the generic get interceptor of class {ClassDefinition} must be {genericParameter.Name}.", _getInterceptor);
                    }
                }
                else
                {
                    if (returnType?.FullName != "System.Object")
                    {
                        throw new WeavingException($"The return type of the get interceptor of class {ClassDefinition} must be System.Object.", _getInterceptor);
                    }
                }
            }

            private MethodDefinition? WhenAccessibleInDerivedClass(MethodDefinition? baseMethodDefinition)
            {
                if (baseMethodDefinition is null)
                    return default;

                if (baseMethodDefinition.IsPrivate)
                {
                    _accessorWeaver._logger.LogWarning($"{baseMethodDefinition} is not accessible from {ClassDefinition}, no properties will be intercepted.");

                    return default;
                }

                return baseMethodDefinition;
            }
        }

        private sealed class ClassWeaver(PropertyAccessorWeaver accessorWeaver, TypeDefinition classDefinition)
        {
            private readonly PropertyAccessorWeaver _accessorWeaver = accessorWeaver;
            private readonly TypeDefinition _classDefinition = classDefinition;

            private readonly ILogger _logger = accessorWeaver._logger;

            private MethodDefinition StaticConstructor
            {
                get
                {
                    var constructor = _classDefinition.GetStaticConstructor();

                    if (constructor is not null)
                        return constructor;

                    const MethodAttributes attributes = MethodAttributes.Private | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName | MethodAttributes.Static;

                    constructor = new MethodDefinition(".cctor", attributes, _accessorWeaver._moduleWeaver.TypeSystem.VoidReference);
                    constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

                    _classDefinition.Methods.Add(constructor);

                    return constructor;
                }
            }

            public void Execute(Interceptors interceptors)
            {
                var getInterceptor = interceptors.GetInterceptor;
                var setInterceptor = interceptors.SetInterceptor;

                if ((getInterceptor == null) && (setInterceptor == null))
                    return;

                _logger.LogInfo($"Intercept auto-properties in {_classDefinition}");
                _logger.LogDebug($"\tGet => {getInterceptor}, Set => {setInterceptor}");

                foreach (var property in _classDefinition.Properties)
                {
                    if (property.CustomAttributes.GetAttribute(AttributeNames.InterceptIgnore) != null)
                    {
                        _logger.LogInfo($"\tSkip {property.Name}, has [InterceptIgnore]");
                        continue;
                    }

                    try
                    {
                        new PropertyWeaver(this, property).Execute(getInterceptor!, setInterceptor!);
                    }
                    catch (WeavingException ex)
                    {
                        _logger.LogError($"Error intercepting {property}: {ex.Message}", ex.Method);
                    }
                }
            }

            private sealed class PropertyWeaver
            {
                private readonly ILogger _logger;
                private readonly ClassWeaver _classWeaver;
                private readonly SystemReferences _systemReferences;

                private readonly ModuleDefinition _moduleDefinition;
                private readonly TypeDefinition _classDefinition;

                private readonly PropertyDefinition _property;
                private FieldDefinition? _propertyInfo;

                private bool _isBackingFieldAccessed;

                private FieldDefinition PropertyInfo
                {
                    get
                    {
                        if (_propertyInfo is not null)
                            return _propertyInfo;

                        _propertyInfo = new FieldDefinition($"<{_property.Name}>k__PropertyInfo", FieldAttributes.InitOnly | FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.CompilerControlled, _classWeaver._accessorWeaver._systemReferences.PropertyInfoType);

                        var declaringType = _property.DeclaringType;

                        declaringType.Fields.Add(_propertyInfo);

                        var getPropertyInfo = _systemReferences.GetPropertyInfo;

                        if (getPropertyInfo is null)
                            throw new WeavingException("The PropertyInfo parameter is not supported in the current framework.");

                        _classWeaver.StaticConstructor.Body.Instructions.InsertRange(0,
                            Instruction.Create(OpCodes.Ldtoken, declaringType.GetReference()),
                            Instruction.Create(OpCodes.Call, _systemReferences.GetTypeFromHandle),
                            Instruction.Create(OpCodes.Ldstr, _property.Name),
                            Instruction.Create(OpCodes.Ldc_I4, (int)(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)),
                            Instruction.Create(OpCodes.Call, getPropertyInfo),
                            Instruction.Create(OpCodes.Stsfld, _propertyInfo.GetReference()));

                        return _propertyInfo;
                    }
                }

                public PropertyWeaver(ClassWeaver classWeaver, PropertyDefinition property)
                {
                    _classWeaver = classWeaver;
                    _property = property;

                    _systemReferences = _classWeaver._accessorWeaver._systemReferences;
                    _moduleDefinition = _classWeaver._accessorWeaver._moduleDefinition;
                    _logger = _classWeaver._accessorWeaver._logger;
                    _classDefinition = _classWeaver._classDefinition;
                }

                public void Execute(MethodDefinition getInterceptor, MethodDefinition setInterceptor)
                {
                    var backingField = _property.FindAutoPropertyBackingField(_classDefinition.Fields);

                    if (backingField is null)
                    {
                        _logger.LogInfo($"\tSkip {_property.Name}, not an auto-property");
                        return;
                    }

                    _logger.LogInfo($"\tIntercept {_property.Name}");

                    var newGetter = BuildGetter(backingField, getInterceptor);
                    var newSetter = BuildSetter(backingField, setInterceptor);

                    foreach (var constructor in _classDefinition.GetConstructors())
                    {
                        constructor.ReplaceFieldAccessWithPropertySetter(backingField, _property, _classWeaver._accessorWeaver._moduleDefinition.SymbolReader);
                    }

                    if (!_isBackingFieldAccessed)
                    {
                        if (_classDefinition.GetConstructors().Any(ctor => ctor.AccessesMember(backingField)))
                        {
                            throw new WeavingException($"The auto-property {_property} is inline initialized and cannot be intercepted.", _property.GetMethod ?? _property.SetMethod);
                        }

                        _logger.LogDebug($"\t\tRemove backing field for {_property.Name}");
                        _classDefinition.Fields.Remove(backingField);
                    }
                    else
                    {
                        _logger.LogDebug($"\t\tPreserve backing field for {_property.Name} because an interceptor uses it.");
                    }

                    _property.GetMethod?.Body?.Instructions.Replace(newGetter);
                    _property.SetMethod?.Body?.Instructions.Replace(newSetter);
                }

                private TypeReference? Import(TypeReference? type) => type == null ? null : _moduleDefinition.ImportReference(type);

                private IEnumerable<Instruction>? BuildGetter(FieldDefinition backingField, MethodDefinition? getInterceptor)
                {
                    var getMethod = _property.GetMethod;

                    if (getMethod is null)
                    {
                        _logger.LogDebug($"\t\tProperty has no getter");

                        return default;
                    }

                    if (getInterceptor is null)
                        throw new WeavingException($"property {_property} has a getter, but the class has no [GetInterceptor].", getMethod);

                    _logger.LogDebug($"\t\tIntercept getter");
                    return BuildInstructions(backingField, getInterceptor, false).ToArray();
                }

                private IEnumerable<Instruction>? BuildSetter(FieldDefinition backingField, MethodDefinition? setInterceptor)
                {
                    var setMethod = _property.SetMethod;

                    if (setMethod is null)
                    {
                        _logger.LogDebug($"\t\tProperty has no setter");

                        return default;
                    }

                    if (setInterceptor is null)
                        throw new WeavingException($"property {_property} has a setter, but the class has no [SetInterceptor].", setMethod);

                    _logger.LogDebug($"\t\tIntercept setter");

                    return BuildInstructions(backingField, setInterceptor, true).ToArray();
                }

                private IEnumerable<Instruction> BuildInstructions(FieldDefinition backingField, MethodDefinition interceptor, bool isSetter)
                {
                    yield return Instruction.Create(OpCodes.Ldarg_0);

                    var propertyType = _property.PropertyType;
                    var parameters = interceptor.Parameters;

                    foreach (var parameter in parameters)
                    {
                        var parameterType = parameter.ParameterType;

                        if (parameterType.IsByReference && parameterType.GetElementType().IsGenericParameter)
                        {
                            _isBackingFieldAccessed = true;

                            yield return Instruction.Create(OpCodes.Ldarg_0);
                            yield return Instruction.Create(OpCodes.Ldflda, backingField.GetReference());
                        }
                        else if (parameterType.IsGenericParameter)
                        {
                            if (isSetter)
                            {
                                yield return Instruction.Create(OpCodes.Ldarg_1);
                            }
                            else
                            {
                                _isBackingFieldAccessed = true;

                                yield return Instruction.Create(OpCodes.Ldarg_0);
                                yield return Instruction.Create(OpCodes.Ldfld, backingField.GetReference());
                            }
                        }
                        else
                        {
                            switch (parameterType.FullName)
                            {
                                case "System.String":
                                    yield return Instruction.Create(OpCodes.Ldstr, _property.Name);
                                    break;

                                case "System.Type":
                                    yield return Instruction.Create(OpCodes.Ldtoken, Import(propertyType));
                                    yield return Instruction.Create(OpCodes.Call, _systemReferences.GetTypeFromHandle);
                                    break;

                                case "System.Reflection.PropertyInfo":
                                    yield return Instruction.Create(OpCodes.Ldsfld, PropertyInfo.GetReference());
                                    break;

                                case "System.Reflection.FieldInfo":
                                    _isBackingFieldAccessed = true;

                                    yield return Instruction.Create(OpCodes.Ldtoken, backingField.GetReference());
                                    yield return Instruction.Create(OpCodes.Call, _systemReferences.GetFieldFromHandle);
                                    break;

                                case "System.Object":
                                    if (isSetter)
                                    {
                                        yield return Instruction.Create(OpCodes.Ldarg_1);
                                    }
                                    else
                                    {
                                        _isBackingFieldAccessed = true;

                                        yield return Instruction.Create(OpCodes.Ldarg_0);
                                        yield return Instruction.Create(OpCodes.Ldfld, backingField.GetReference());
                                    }

                                    if (propertyType.IsGenericParameter || propertyType.IsValueType)
                                    {
                                        // we only need to box for value types or if type is generic (no cast for reference)
                                        yield return Instruction.Create(OpCodes.Box, Import(propertyType));
                                    }

                                    break;

                                default:
                                    throw new WeavingException($"A parameter of type {parameterType} in the interceptor {interceptor} is not supported.", interceptor);
                            }
                        }
                    }

                    if (interceptor.ContainsGenericParameter)
                    {
                        if (interceptor.GenericParameters.Count != 1)
                            throw new WeavingException($"Only one generic parameter is supported in the interceptor {interceptor}.", interceptor);

                        var generic = new GenericInstanceMethod(interceptor.GetReference(_classDefinition));

                        generic.GenericArguments.Add(Import(propertyType));

                        yield return Instruction.Create(OpCodes.Call, generic);
                    }
                    else
                    {
                        yield return Instruction.Create(OpCodes.Call, interceptor.GetReference(_classDefinition));

                        if (!isSetter)
                        {
                            yield return propertyType.IsValueType ? Instruction.Create(OpCodes.Unbox_Any, Import(propertyType)) : Instruction.Create(OpCodes.Castclass, Import(propertyType));
                        }
                    }

                    yield return Instruction.Create(OpCodes.Ret);
                }
            }
        }

        private sealed class ClassFinalizeWeaver(PropertyAccessorWeaver accessorWeaver, TypeDefinition classDefinition)
        {
            private const MethodAttributes Attributes = MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig;    // protected override

            private const string IFinalizable = $"{nameof(System)}.{nameof(IFinalizable)}";
            private const string IDisposable = $"{nameof(System)}.{nameof(IDisposable)}";

            private const string OnFinalize = nameof(OnFinalize);
            private const string OnDispose = nameof(OnDispose);

            private const string Extensions = "𝓔𝔁𝓽𝓮𝓷𝓼𝓲𝓸𝓷𝓼";
            private const string Disposable = "𝓭𝓲𝓼𝓹𝓸𝓼𝓪𝓫𝓵𝓮";
            private const string Dispose = "𝓓𝓲𝓼𝓹𝓸𝓼𝓮";

            private readonly PropertyAccessorWeaver _accessorWeaver = accessorWeaver;
            private readonly TypeDefinition _classDefinition = classDefinition;
            private readonly ILogger _logger = accessorWeaver._logger;

            [SuppressMessage("Style", "IDE0060:删除未使用的参数", Justification = "<挂起>")]
            public void Execute(Interceptors interceptors)
            {
                if (_classDefinition.IsStatic() || !_classDefinition.HasFields)
                    return;

                if (FindFinalizableBase(_classDefinition) is not TypeDefinition finalizableBase)
                    return;

                if (FindDisposeExtensionMethod(finalizableBase, out var onDisposeMethod, out var onFinalizeMethod) is not MethodReference disposeMethodReference)
                    return;

                //OverrideOnDisposeMethod(onDisposeMethod, disposeMethodReference);
                OverrideOnFinalizeMethod(onFinalizeMethod, disposeMethodReference);
            }

            private TypeDefinition? FindFinalizableBase(TypeDefinition? classDefinition)
            {
                if (classDefinition is null)
                    return default;

                foreach (var interfaceImplementation in classDefinition.Interfaces)
                {
                    if (interfaceImplementation.InterfaceType.FullName is IFinalizable)
                    {
                        if (classDefinition.BaseType is TypeReference baseTypeReference)
                        {
                            if (FindFinalizableBase(baseTypeReference.Resolve()) is not null)
                            {
                                _logger.LogError($"“{IFinalizable}”已在“{classDefinition.FullName}”的基类“{baseTypeReference.FullName}”的接口列表中列出。", classDefinition.GetPoint());

                                return default;
                            }
                        }

                        return classDefinition;
                    }
                }

                return FindFinalizableBase(classDefinition.BaseType?.Resolve());
            }

            private MethodReference? FindDisposeExtensionMethod(TypeDefinition finalizableBase, out MethodDefinition? onDisposeMethod, out MethodDefinition? onFinalizeMethod)
            {
                onDisposeMethod = default;
                onFinalizeMethod = default;

                foreach (var methodDefinition in _classDefinition.Methods)
                {
                    /*if (CheckSignature(finalizableBase, methodDefinition, OnDispose))
                    {
                        onDisposeMethod = methodDefinition;
                    }
                    else*/ if (CheckSignature(finalizableBase, methodDefinition, OnFinalize))
                    {
                        onFinalizeMethod = methodDefinition;
                    }

                    if (/*(onDisposeMethod is not null) && */(onFinalizeMethod is not null))
                    {
                        return _classDefinition.ImportReference(CreateDisposeMethod(finalizableBase));
                    }
                }

                if (finalizableBase != _classDefinition)
                {
                    return _classDefinition.ImportReference(FindDisposeMethod(finalizableBase) ?? throw new SymbolsNotFoundException($"“{finalizableBase.Module.Name}”中未找到名为“{Dispose}”的静态扩展方法。"));
                }

                //if (onDisposeMethod is null)
                //{
                //    _logger.LogError($"“{finalizableBase.FullName}”中未定义签名为 void {OnDispose}() 的虚拟方法。");

                //    return default;
                //}

                if (onFinalizeMethod is null)
                {
                    _logger.LogError($"“{finalizableBase.FullName}”中未定义签名为 void {OnFinalize}() 的虚拟方法。");

                    return default;
                }

                throw new InvalidOperationException($"“{_classDefinition.FullName}”中发生不可预知的错误。");

                bool CheckSignature(TypeDefinition finalizableBase, MethodDefinition methodDefinition, string methodName)
                {
                    const MethodAttributes Attributes = MethodAttributes.Virtual | MethodAttributes.HideBySig;

                    if (methodDefinition.Name != methodName)
                        return default;

                    var attributes = methodDefinition.Attributes;

                    if (((attributes & Attributes) is Attributes) && (methodDefinition.ReturnType is { MetadataType: MetadataType.Void }) && (methodDefinition.HasParameters is not true))
                    {
                        if (finalizableBase == _classDefinition)
                        {
                            if ((attributes & MethodAttributes.Abstract) is MethodAttributes.Abstract)
                            {
                                _logger.LogError($"“{methodDefinition.FullName}”方法应该是虚拟的，而不是抽象的。");

                                return default;
                            }
                        }
                        else
                        {
                            _logger.LogError($"不应在派生类中显式重写“{methodDefinition.Name}”虚拟方法。", methodDefinition);

                            return default;
                        }
                    }
                    else
                    {
                        if ((finalizableBase != _classDefinition) && (methodDefinition.HasParameters is not true))
                        {
                            _logger.LogError($"类型“{_classDefinition.FullName}”已定义了一个名为“{methodDefinition.Name}”的具有相同参数类型的成员。", methodDefinition);

                            // 类型“{_classDefinition.FullName}”已经包含“{OnDispose}”的定义。
                        }

                        return default;
                    }

                    return (attributes & MethodAttributes.NewSlot) is MethodAttributes.NewSlot;
                }
            }

            private void OverrideOnDisposeMethod(MethodDefinition? onDisposeMethod, MethodReference disposeMethodReference)
            {
                if (onDisposeMethod is null)
                {
                    onDisposeMethod = CreateOverrideMethod(OnDispose, out var baseMethodReference);

                    if (DisposeObjects(onDisposeMethod, disposeMethodReference) is not { Count: not 0 } instructions)
                        return;

                    var overrideInstructions = onDisposeMethod.Body.Instructions;

                    overrideInstructions.AddRange(instructions);

                    overrideInstructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    overrideInstructions.Add(Instruction.Create(OpCodes.Call, baseMethodReference));    // base.OnDispose();
                    overrideInstructions.Add(Instruction.Create(OpCodes.Ret));

                    _classDefinition.Methods.Add(onDisposeMethod);
                }
                else
                {
                    var declaringInstructions = onDisposeMethod.Body.Instructions;

                    declaringInstructions.Clear();
                    declaringInstructions.AddRange(DisposeObjects(onDisposeMethod, disposeMethodReference));

                    declaringInstructions.Add(Instruction.Create(OpCodes.Ret));
                }
            }

            private void OverrideOnFinalizeMethod(MethodDefinition? onFinalizeMethod, MethodReference disposeMethodReference)
            {
                if (onFinalizeMethod is null)
                {
                    onFinalizeMethod = CreateOverrideMethod(OnFinalize, out var baseMethodReference);

                    if (FinalizeFields(onFinalizeMethod, disposeMethodReference) is not { Count: not 0 } instructions)
                        return;

                    var overrideInstructions = onFinalizeMethod.Body.Instructions;

                    overrideInstructions.AddRange(instructions);

                    overrideInstructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    overrideInstructions.Add(Instruction.Create(OpCodes.Call, baseMethodReference));    // base.OnFinalize();
                    overrideInstructions.Add(Instruction.Create(OpCodes.Ret));

                    _classDefinition.Methods.Add(onFinalizeMethod);
                }
                else
                {
                    var declaringInstructions = onFinalizeMethod.Body.Instructions;

                    declaringInstructions.Clear();
                    declaringInstructions.AddRange(FinalizeFields(onFinalizeMethod, disposeMethodReference));

                    declaringInstructions.Add(Instruction.Create(OpCodes.Ret));
                }
            }

            private IList<Instruction> DisposeObjects(MethodDefinition onDisposeMethod, MethodReference disposeMethodReference)
            {
                var instructions = new List<Instruction>();

                foreach (var fieldDefinition in _classDefinition.Fields)
                {
                    if (fieldDefinition.IsStatic)
                        continue;

                    var fieldType = fieldDefinition.FieldType;

                    if (!IsAssignableInterface(fieldType.Resolve(), IDisposable))
                        continue;

                    instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    instructions.Add(Instruction.Create(OpCodes.Ldfld, fieldDefinition.GetReference()));

                    if (fieldType.IsValueType)
                    {
                        instructions.Add(Instruction.Create(OpCodes.Box, fieldType));
                    }

                    instructions.Add(Instruction.Create(OpCodes.Call, disposeMethodReference));
                }

                return instructions.AsReadOnly();
            }

            private IList<Instruction> FinalizeFields(MethodDefinition onFinalizeMethod, MethodReference disposeMethodReference)
            {
                var instructions = new List<Instruction>();
                var variables = onFinalizeMethod.Body.Variables;

                foreach (var fieldDefinition in _classDefinition.Fields)
                {
                    if (fieldDefinition.IsStatic)
                        continue;

                    var fieldType = fieldDefinition.FieldType;

                    if (fieldType.IsValueType || (fieldType is RequiredModifierType { ElementType.IsValueType: true }))
                        continue;

                    instructions.Add(Instruction.Create(OpCodes.Ldarg_0));

                    if (fieldType.IsRequiredModifier)
                    {
                        if (fieldType is RequiredModifierType { ElementType: { IsGenericParameter: true } elementType })
                        {
                            var variable = new VariableDefinition(elementType);

                            variables.Add(variable);

                            instructions.Add(Instruction.Create(OpCodes.Ldloca_S, variable));
                            instructions.Add(Instruction.Create(OpCodes.Initobj, elementType));

                            instructions.Add(Instruction.Create(OpCodes.Ldloc, variable));
                            instructions.Add(Instruction.Create(OpCodes.Volatile));

                            instructions.Add(Instruction.Create(OpCodes.Stfld, fieldDefinition.GetReference()));
                        }
                        else
                        {
                            instructions.Add(Instruction.Create(OpCodes.Ldnull));
                            instructions.Add(Instruction.Create(OpCodes.Volatile));
                            instructions.Add(Instruction.Create(OpCodes.Stfld, fieldDefinition.GetReference()));
                        }
                    }
                    else
                    {
                        if (fieldType.IsGenericParameter)
                        {
                            instructions.Add(Instruction.Create(OpCodes.Ldflda, fieldDefinition.GetReference()));
                            instructions.Add(Instruction.Create(OpCodes.Initobj, fieldType));
                        }
                        else
                        {
                            instructions.Add(Instruction.Create(OpCodes.Ldnull));
                            instructions.Add(Instruction.Create(OpCodes.Stfld, fieldDefinition.GetReference()));
                        }
                    }
                }

                return instructions.AsReadOnly();
            }

            private MethodDefinition CreateDisposeMethod(TypeDefinition classDefinition)
            {
                var moduleDefinition = _accessorWeaver._moduleDefinition;

                if (!moduleDefinition.TryGetTypeReference(default, IDisposable, out var parameterType))
                    throw new SymbolsNotFoundException(IDisposable);

                var extensionsTypeDefinition = new TypeDefinition(default, Extensions, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit, _accessorWeaver._moduleWeaver.TypeSystem.ObjectReference);
                var disposeMethodDefinition = new MethodDefinition(Dispose, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, _accessorWeaver._moduleWeaver.TypeSystem.VoidReference);

                var nullableContextAttribute = GetNullableContextAttribute(moduleDefinition);
                var extensionAttribute = GetExtensionAttribute(moduleDefinition);

                disposeMethodDefinition.CustomAttributes.Add(extensionAttribute);
                disposeMethodDefinition.CustomAttributes.Add(nullableContextAttribute);

                disposeMethodDefinition.Parameters.Add(new(Disposable, default, parameterType));

                var instructions = disposeMethodDefinition.Body.Instructions;
                var ret = Instruction.Create(OpCodes.Ret);

                instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                instructions.Add(Instruction.Create(OpCodes.Brfalse_S, ret));

                instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                instructions.Add(Instruction.Create(OpCodes.Callvirt, moduleDefinition.ImportReference(parameterType.Resolve().Methods[0])));

                instructions.Add(ret);

                extensionsTypeDefinition.CustomAttributes.Add(extensionAttribute);
                extensionsTypeDefinition.Methods.Add(disposeMethodDefinition);
                classDefinition.Module.Types.Add(extensionsTypeDefinition);

                return disposeMethodDefinition;
            }

            private MethodDefinition CreateOverrideMethod(string methodName, out MethodReference baseMethodReference)
            {
                if (FindBaseMethod(_classDefinition.BaseType.Resolve(), methodName) is not MethodDefinition baseMethodDefinition)
                    throw new SymbolsNotFoundException($"在“{_classDefinition.FullName}”的基类中未找到“{methodName}”方法。");

                baseMethodReference = _classDefinition.ImportReference(baseMethodDefinition.MakeGeneric(_classDefinition.BaseType));

                return new(methodName, baseMethodDefinition.Attributes & ~MethodAttributes.NewSlot, baseMethodDefinition.ReturnType);
            }

            private CustomAttribute GetExtensionAttribute(ModuleDefinition moduleDefinition)
            {
                const string Name = "System.Runtime.CompilerServices.ExtensionAttribute";

                if (moduleDefinition.GetType(Name) is not TypeDefinition typeDefinition)
                    typeDefinition = _accessorWeaver._moduleWeaver.FindTypeDefinition(Name);

                return new(moduleDefinition.ImportReference(typeDefinition.GetConstructors().First()), [0x01, 0x00, 0x00, 0x00]);
            }

            private CustomAttribute GetNullableContextAttribute(ModuleDefinition moduleDefinition)
            {
                const string Name = "System.Runtime.CompilerServices.NullableContextAttribute";

                if (moduleDefinition.GetType(Name) is not TypeDefinition typeDefinition)
                    typeDefinition = _accessorWeaver._moduleWeaver.FindTypeDefinition(Name);

                return new(moduleDefinition.ImportReference(typeDefinition.GetConstructors().First()), [0x01, 0x00, 0x02, 0x00, 0x00]);
            }

            private MethodDefinition? FindBaseMethod(TypeDefinition? typeDefinition, string methodName)
            {
                const MethodAttributes Attributes = MethodAttributes.Virtual | MethodAttributes.HideBySig;

                if (typeDefinition is null)
                    return default;

                foreach (var methodDefinition in typeDefinition.Methods)
                {
                    if ((methodDefinition.Name == methodName) && (methodDefinition.HasParameters is not true))
                    {
                        if ((methodDefinition.Attributes & Attributes) == Attributes)
                        {
                            return methodDefinition;
                        }
                    }
                }

                return FindBaseMethod(typeDefinition.BaseType?.Resolve(), methodName);
            }

            private static bool IsAssignableInterface(TypeDefinition? classDefinition, string to)
            {
                if (classDefinition is null)
                    return default;

                foreach (var interfaceImplementation in classDefinition.Interfaces)
                {
                    if (interfaceImplementation.InterfaceType.FullName == to)
                    {
                        return true;
                    }
                }

                return IsAssignableInterface(classDefinition.BaseType?.Resolve(), to);
            }

            private static MethodDefinition? FindDisposeMethod(TypeDefinition? classDefinition)
            {
                if (classDefinition is null)
                    return default;

                if (classDefinition.Module.GetType(default, Extensions) is TypeDefinition extensionsTypeDefinition)
                {
                    foreach (var methodDefinition in extensionsTypeDefinition.Methods)
                    {
                        if (methodDefinition.Name == Dispose)
                        {
                            return methodDefinition;
                        }
                    }
                }

                return FindDisposeMethod(classDefinition.BaseType?.Resolve());
            }
        }
    }
}
