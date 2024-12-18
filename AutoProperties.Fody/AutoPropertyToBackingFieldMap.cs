using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

using Mono.Cecil;

namespace AutoProperties.Fody
{
    internal sealed class AutoPropertyToBackingFieldMap(TypeDefinition classDefinition)
    {
        private readonly TypeDefinition _classDefinition = classDefinition;

        private IDictionary<string, AutoPropertyInfo>? _map;

        public bool TryGetValue(string propertyName, [NotNullWhen(true)] out AutoPropertyInfo? value)
        {
            _map ??= CreateMap();

            return _map.TryGetValue(propertyName, out value);
        }

        private IDictionary<string, AutoPropertyInfo> CreateMap() => CreateMap(_classDefinition.Properties, _classDefinition.Fields);

        private static ReadOnlyDictionary<string, AutoPropertyInfo> CreateMap(ICollection<PropertyDefinition> properties, ICollection<FieldDefinition> fields)
        {
            var map = new Dictionary<string, AutoPropertyInfo>();

            foreach (var property in properties)
            {
                if (property.FindAutoPropertyBackingField(fields) is FieldDefinition fieldDefinition)
                {
                    map.Add(property.Name, new(fieldDefinition.GetReference(), property));
                }
            }

            return new(map);
        }
    }

    internal sealed class AutoPropertyInfo(FieldReference backingField, PropertyDefinition property)
    {
        public FieldReference BackingField { get; } = backingField;

        public PropertyDefinition Property { get; } = property;
    }
}
