using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using TestScopeService.Abstrations.Dtos;
using TransactionManager.Abstraction.Services;

namespace TestScopeService.Implementations.Factories
{
    /// <summary>
    /// Generates deterministic file names that include the person's surname, name, and a stable hash.
    /// </summary>
    public sealed class PersonFileNameFactory : IFileNameFactory
    {
        private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars();
        private readonly ILogger<PersonFileNameFactory> _logger;

        public PersonFileNameFactory(ILogger<PersonFileNameFactory> logger)
        {
            _logger = logger;
        }

        public string Create(object value, string normalizedId, Type contractType)
        {
            if (value is not IPerson person)
            {
                _logger.LogWarning(
                    "File-name factory for {Contract} received unexpected type {Type}. Falling back to normalized id.",
                    contractType.Name,
                    value?.GetType().FullName ?? "<null>");
                return normalizedId;
            }

            var surname = Sanitize(person.Surname);
            var name = Sanitize(person.Name);

            var key = string.Concat(surname, "|", name);
            if (string.IsNullOrWhiteSpace(key.Trim('|')))
            {
                return normalizedId;
            }

            var hash = ComputeStableSuffix(key);

            var segments = new[] { surname, name }
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .Append(hash)
                .ToArray();

            var candidate = string.Join("-", segments);
            return candidate.Length == 0 ? normalizedId : candidate;
        }

        private static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(value.Length);
            var lastWasHyphen = false;

            foreach (var ch in value)
            {
                if (char.IsWhiteSpace(ch) || ch == '-')
                {
                    if (!lastWasHyphen && builder.Length > 0)
                    {
                        builder.Append('-');
                        lastWasHyphen = true;
                    }

                    continue;
                }

                if (InvalidCharacters.Contains(ch))
                {
                    continue;
                }

                builder.Append(char.ToLowerInvariant(ch));
                lastWasHyphen = false;
            }

            return builder.ToString().Trim('-');
        }

        private static string ComputeStableSuffix(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var hash = SHA256.HashData(bytes);
            // first 8 hex chars -> 32 bits of entropy, stable across runs.
            var hex = Convert.ToHexString(hash.AsSpan(0, 4));
            return hex.ToLowerInvariant();
        }
    }
}
