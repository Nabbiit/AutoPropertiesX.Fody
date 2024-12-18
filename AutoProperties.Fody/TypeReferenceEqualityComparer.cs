using System.Collections.Generic;

using Mono.Cecil;

namespace AutoProperties.Fody
{
    internal sealed class TypeReferenceEqualityComparer : IEqualityComparer<TypeReference>
    {
        public static IEqualityComparer<TypeReference> Default { get; } = new TypeReferenceEqualityComparer();

        private TypeReferenceEqualityComparer() {; }

        public bool Equals(TypeReference x, TypeReference y) => x?.Resolve() == y?.Resolve();

        public int GetHashCode(TypeReference obj) => obj.Resolve()?.GetHashCode() ?? default;
    }
}
