// IProviderBootstrap has been removed.
// QueryEvaluator now loads all user assemblies (including EF Core) into the
// user's isolated ALC and uses CSharpCompilation + LoadFromStream so that
// no EF Core types cross ALC boundaries. Provider detection is done by
// scanning the assemblies loaded in the user's ALC.
// See QueryEvaluator.cs for the current architecture.
namespace EFQueryLens.Core;

