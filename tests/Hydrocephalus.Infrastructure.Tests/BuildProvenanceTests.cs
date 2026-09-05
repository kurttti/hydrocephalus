using System.Reflection;
using System.Reflection.Emit;
using Hydrocephalus.Infrastructure.Configuration;

namespace Hydrocephalus.Infrastructure.Tests;

/// <summary>
/// Чтение commit SHA сборки для provenance отчёта.
///
/// Отчёт заявляет воспроизводимость, поэтому поле, заполненное заглушкой, хуже
/// отсутствующего: заглушка выглядит как заполненное. Сборка без SHA — это
/// сборка, результат которой невозможно связать с кодом, и говорить об этом
/// нужно на старте приложения, а не в отчёте.
/// </summary>
public sealed class BuildProvenanceTests
{
    private const string Sha = "ca4eee44bc298be534055ddf058656aac7d5e767";

    [Fact]
    public void The_sha_is_read_from_the_informational_version()
    {
        var assembly = AssemblyWith($"1.0.0+{Sha}");

        Assert.True(BuildProvenance.TryGetCommitSha(assembly, out var sha));
        Assert.Equal(Sha, sha);
    }

    [Fact]
    public void A_build_without_a_sha_is_reported_and_not_papered_over()
    {
        // Ровно то, что произойдёт при сборке из архива без .git.
        var assembly = AssemblyWith("1.0.0");

        Assert.False(BuildProvenance.TryGetCommitSha(assembly, out _));
        Assert.Throws<InvalidOperationException>(() => BuildProvenance.CommitShaOf(assembly));
    }

    [Theory]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0+not-a-sha")]
    [InlineData("1.0.0+ca4eee4")]
    [InlineData("1.0.0+zzzeee44bc298be534055ddf058656aac7d5e767")]
    public void Anything_that_is_not_a_full_sha_is_refused(string informationalVersion)
    {
        // Короткий SHA и метка ветки встречаются в чужих схемах версий и не годятся:
        // provenance должен называть коммит однозначно.
        Assert.False(BuildProvenance.TryGetCommitSha(AssemblyWith(informationalVersion), out _));
    }

    [Fact]
    public void A_version_with_a_prerelease_tag_still_yields_the_sha()
    {
        // SemVer допускает и предрелизную метку, и метаданные сборки:
        // SHA идёт после последнего «+».
        var assembly = AssemblyWith($"1.2.3-rc.1+{Sha}");

        Assert.True(BuildProvenance.TryGetCommitSha(assembly, out var sha));
        Assert.Equal(Sha, sha);
    }

    [Fact]
    public void This_build_carries_its_own_sha()
    {
        // Проверка того, что SDK действительно проставляет атрибут: без неё
        // приложение упало бы при первом запуске, а не здесь.
        Assert.True(
            BuildProvenance.TryGetCommitSha(typeof(BuildProvenance).Assembly, out var sha),
            "The build carries no commit SHA; the composition root would refuse to start.");

        Assert.Equal(40, sha.Length);
    }

    private static AssemblyBuilder AssemblyWith(string informationalVersion)
    {
        var name = new AssemblyName("Provenance" + Guid.NewGuid().ToString("N"));

        var builder = AssemblyBuilder.DefineDynamicAssembly(name, AssemblyBuilderAccess.Run);

        builder.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            [informationalVersion]));

        return builder;
    }
}
