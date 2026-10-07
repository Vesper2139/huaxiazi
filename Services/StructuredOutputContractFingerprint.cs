using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Huaxiazi.Services;

/// <summary>Hashes the provider-neutral output contract, including local validation rules.</summary>
public static class StructuredOutputContractFingerprint
{
    public static string Compute(StructuredOutputContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);

        var fieldTypes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in contract.AllowedFields.OrderBy(value => value, StringComparer.Ordinal))
        {
            var type = contract.FieldTypes is not null && contract.FieldTypes.TryGetValue(field, out var declaredType)
                ? declaredType
                : StructuredOutputFieldType.String;
            fieldTypes[field] = type.ToString();
        }

        var allowedStringValues = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        if (contract.AllowedStringValues is not null)
        {
            foreach (var pair in contract.AllowedStringValues.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                allowedStringValues[pair.Key] = pair.Value.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }

        var numericRanges = new SortedDictionary<string, StructuredOutputNumericRange>(StringComparer.Ordinal);
        if (contract.NumericRanges is not null)
        {
            foreach (var pair in contract.NumericRanges.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                numericRanges[pair.Key] = pair.Value;
        }

        var canonicalContract = new
        {
            required_fields = contract.RequiredFields.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            allowed_fields = contract.AllowedFields.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            max_answer_characters = contract.MaxAnswerCharacters,
            field_types = fieldTypes,
            allowed_string_values = allowedStringValues,
            numeric_ranges = numericRanges
        };
        var canonicalJson = JsonSerializer.Serialize(canonicalContract);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
    }
}
