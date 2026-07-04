using Xunit;

namespace Klaxon.Tests.Integration.Infrastructure;

[CollectionDefinition("Api")]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
}
