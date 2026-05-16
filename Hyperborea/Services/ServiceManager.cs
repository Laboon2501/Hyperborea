using ECommons.Singletons;
using Hyperborea.Services.OpcodeUpdaterService;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hyperborea.Services;
public static class S
{
    public static ThreadPool ThreadPool { get; private set; } = null!;
    public static OpcodeUpdater OpcodeUpdater { get; private set; } = null!;
    static TerritoryDiscoveryService? territoryDiscovery;
    public static TerritoryDiscoveryService TerritoryDiscovery
    {
        get => territoryDiscovery ??= new();
        private set => territoryDiscovery = value;
    }
    static BgPathResolver? bgPathResolver;
    public static BgPathResolver BgPathResolver
    {
        get => bgPathResolver ??= new();
        private set => bgPathResolver = value;
    }
}
