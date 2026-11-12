namespace PostalService.BuildingBlocks.Application;

public static class Roles
{
    public const string Administrator = "Administrator";
    public const string PostOfficeManager = "PostOfficeManager";
    public const string ShipmentOperator = "ShipmentOperator";
    public const string ShipmentViewer = "ShipmentViewer";

    public static readonly IReadOnlyList<string> All =
        new[] { Administrator, PostOfficeManager, ShipmentOperator, ShipmentViewer };
}

/// <summary>Fine-grained permissions. Services authorise on these, never on role names.</summary>
public static class Permissions
{
    public const string ShipmentsRead = "shipments.read";
    public const string ShipmentsWrite = "shipments.write";
    public const string ShipmentsDelete = "shipments.delete";
    public const string ShipmentsUpdateStatus = "shipments.status";
    public const string ShipmentsCorrectStatus = "shipments.correct";
    public const string PostOfficesRead = "postoffices.read";
    public const string PostOfficesWrite = "postoffices.write";
    public const string PostOfficesDelete = "postoffices.delete";
    public const string UsersManage = "users.manage";

    public static readonly IReadOnlyList<string> All = new[]
    {
        ShipmentsRead, ShipmentsWrite, ShipmentsDelete, ShipmentsUpdateStatus, ShipmentsCorrectStatus,
        PostOfficesRead, PostOfficesWrite, PostOfficesDelete, UsersManage
    };
}

public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<string, string[]> Map = new Dictionary<string, string[]>
    {
        [Roles.Administrator] = Permissions.All.ToArray(),
        [Roles.PostOfficeManager] = new[]
        {
            Permissions.ShipmentsRead, Permissions.ShipmentsWrite, Permissions.ShipmentsDelete,
            Permissions.ShipmentsUpdateStatus, Permissions.PostOfficesRead, Permissions.PostOfficesWrite
        },
        [Roles.ShipmentOperator] = new[]
        {
            Permissions.ShipmentsRead, Permissions.ShipmentsWrite, Permissions.ShipmentsUpdateStatus,
            Permissions.PostOfficesRead
        },
        [Roles.ShipmentViewer] = new[] { Permissions.ShipmentsRead, Permissions.PostOfficesRead },
    };

    public static IReadOnlyList<string> For(string role) =>
        Map.TryGetValue(role, out var permissions) ? permissions : Array.Empty<string>();
}

/// <summary>Claim types shared by the issuer (Identity) and all resource services.</summary>
public static class PostalClaims
{
    public const string Permission = "perm";
    public const string Role = "role";
    public const string UserName = "name";
    public const string Subject = "sub";
}
