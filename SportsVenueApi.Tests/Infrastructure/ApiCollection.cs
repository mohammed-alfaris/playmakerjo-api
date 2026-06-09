namespace SportsVenueApi.Tests.Infrastructure;

[CollectionDefinition("Api", DisableParallelization = true)]
public class ApiCollection : ICollectionFixture<DatabaseFixture>
{
}
