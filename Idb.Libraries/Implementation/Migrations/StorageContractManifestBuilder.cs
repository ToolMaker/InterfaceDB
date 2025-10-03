using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Abstractions.Migrations;

namespace Idb.Libraries.Implementation.Migrations
{
    public sealed class StorageContractManifestBuilder
    {
        private readonly NullabilityInfoContext _nullability = new();

        public IStorageContractManifest Build(Type contractType, string versionTag)
        {
            if (contractType is null)
            {
                throw new ArgumentNullException(nameof(contractType));
            }

            if (!contractType.IsInterface)
            {
                throw new ArgumentException($"Contract type '{contractType.FullName}' must be an interface.", nameof(contractType));
            }

            if (string.IsNullOrWhiteSpace(versionTag))
            {
                throw new ArgumentException("Version tag cannot be null or whitespace.", nameof(versionTag));
            }

            var members = contractType
                .GetProperties()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .Select(CreateMember)
                .Cast<IStorageContractMember>()
                .ToArray();

            var hash = ComputeHash(contractType, versionTag, members);

            return new StorageContractManifest
            {
                ContractTypeName = contractType.FullName ?? contractType.Name,
                ContractAssemblyName = contractType.Assembly.FullName ?? contractType.Assembly.GetName().Name ?? string.Empty,
                VersionTag = versionTag,
                CreatedAt = DateTimeOffset.UtcNow,
                Members = members,
                Hash = hash
            };
        }

        private StorageContractMember CreateMember(PropertyInfo property)
        {
            var nullability = _nullability.Create(property);
            var type = property.PropertyType;
            var isCollection = type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
            var isNullable = nullability.ReadState == NullabilityState.Nullable;

            return new StorageContractMember(
                property.Name,
                type.FullName ?? type.Name,
                isCollection,
                isNullable);
        }

        private static string ComputeHash(Type contractType, string versionTag, IReadOnlyList<IStorageContractMember> members)
        {
            using var sha = SHA256.Create();
            var builder = new StringBuilder();

            builder.Append(contractType.FullName);
            builder.Append('|');
            builder.Append(versionTag);

            foreach (var member in members)
            {
                builder.Append("||");
                builder.Append(member.Name);
                builder.Append('|');
                builder.Append(member.MemberTypeFullName);
                builder.Append('|');
                builder.Append(member.IsCollection ? '1' : '0');
                builder.Append('|');
                builder.Append(member.IsNullable ? '1' : '0');
            }

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            var hash = sha.ComputeHash(bytes);

            return Convert.ToHexString(hash);
        }
    }
}

