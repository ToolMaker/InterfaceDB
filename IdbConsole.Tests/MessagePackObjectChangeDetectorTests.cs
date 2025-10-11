using System;
using Idb.Libraries.Abstractions.Attributes;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Services.Serialization;

namespace IdbConsole.Tests;

public sealed class MessagePackObjectChangeDetectorTests
{
    private readonly IObjectChangeDetector _detector = new MessagePackObjectChangeDetector();

    [Fact]
    public void ComputeFingerprint_SameObject_ReturnsStableHash()
    {
        var person = new PersonFingerprintContract
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Age = 36,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        var fingerprint1 = _detector.ComputeFingerprint(person, typeof(PersonFingerprintContract));
        var fingerprint2 = _detector.ComputeFingerprint(person, typeof(PersonFingerprintContract));

        Assert.Equal(fingerprint1, fingerprint2);
        Assert.False(_detector.HasChanged(fingerprint1, fingerprint2));
        Assert.NotEqual(default, fingerprint1);
    }

    [Fact]
    public void HasChanged_WhenBusinessPropertyChanges_ReturnsTrue()
    {
        var original = new PersonFingerprintContract { FirstName = "Grace", LastName = "Hopper", Age = 50 };
        var updated = new PersonFingerprintContract { FirstName = "Grace", LastName = "Hopper", Age = 51 };

        var baseline = _detector.ComputeFingerprint(original, typeof(PersonFingerprintContract));
        var next = _detector.ComputeFingerprint(updated, typeof(PersonFingerprintContract));

        Assert.True(_detector.HasChanged(baseline, next));
        Assert.NotEqual(baseline, next);
    }

    [Fact]
    public void HasChanged_WhenIgnoredPropertyDiffers_ReturnsFalse()
    {
        var before = new PersonFingerprintContract { FirstName = "Alan", LastName = "Turing", Age = 41, UpdatedAt = DateTimeOffset.UtcNow };
        var after = new PersonFingerprintContract
        {
            FirstName = before.FirstName,
            LastName = before.LastName,
            Age = before.Age,
            UpdatedAt = before.UpdatedAt.AddMinutes(5)
        };

        var baseline = _detector.ComputeFingerprint(before, typeof(PersonFingerprintContract));
        var next = _detector.ComputeFingerprint(after, typeof(PersonFingerprintContract));

        Assert.False(_detector.HasChanged(baseline, next));
        Assert.Equal(baseline, next);
    }
}

public sealed class PersonFingerprintContract
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Age { get; set; }

    [HashIgnore]
    public DateTimeOffset UpdatedAt { get; set; }
}
