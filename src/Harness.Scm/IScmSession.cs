namespace Harness.Scm;

/// <summary>
/// What tools acting on the current project's SCM need: its resolution, the machine's SCM connections (for tokens)
/// and the providers to call them with.
/// </summary>
public interface IScmSession : IScmProjectContext
{
    IScmConfiguration ScmConfiguration { get; }

    /// <summary>The providers harness ships; null where the host did not supply them.</summary>
    ScmProviderRegistry? ScmProviders { get; }
}
