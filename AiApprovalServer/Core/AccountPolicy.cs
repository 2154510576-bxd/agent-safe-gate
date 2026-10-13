namespace AiApproval.Core;

//权限键，真正判断在服务端
public static class Perm
{
    public const string AuthLogin = "auth.login";

    //账号管理权限键
    public const string AccountCreate = "account.create";
    public const string AccountList = "account.list";
    public const string AccountManage = "account.manage";       //改角色、停用、重置他人口令
    public const string AccountGrantAdmin = "account.grant_admin"; //授予或撤销管理员
    public const string AccountDelete = "account.delete";

    //报销业务权限键
    public const string InvoiceReadSelf = "invoice.read.self";
    public const string InvoiceReadAll = "invoice.read.all";
    public const string InvoiceSubmit = "invoice.submit";
    public const string InvoiceReview = "invoice.review";
    public const string InvoiceUpdate = "invoice.update";
    public const string InvoiceDelete = "invoice.delete";

    //日志与回退权限键
    public const string LogRead = "log.read";
    public const string LogRollback = "log.rollback";

    //管理端只读分析
    public const string AdminRead = "admin.read";
}

//账号与权限策略，返回拒绝原因
public static class AccountPolicy
{
    private static readonly Dictionary<string, HashSet<string>> Matrix = new(StringComparer.Ordinal)
    {
        [Roles.SuperAdmin] = new(StringComparer.Ordinal)
        {
            Perm.AuthLogin, Perm.AccountCreate, Perm.AccountList, Perm.AccountManage,
            Perm.AccountGrantAdmin, Perm.AccountDelete,
            Perm.InvoiceReadSelf, Perm.InvoiceReadAll, Perm.InvoiceSubmit, Perm.InvoiceReview,
            Perm.InvoiceUpdate, Perm.InvoiceDelete,
            Perm.LogRead, Perm.LogRollback, Perm.AdminRead,
        },
        [Roles.Admin] = new(StringComparer.Ordinal)
        {
            Perm.AuthLogin, Perm.AccountCreate, Perm.AccountList, Perm.AccountManage,
            Perm.InvoiceReadSelf, Perm.InvoiceReadAll, Perm.InvoiceSubmit, Perm.InvoiceReview,
            Perm.InvoiceUpdate, Perm.InvoiceDelete,
            Perm.LogRead, Perm.LogRollback, Perm.AdminRead,
        },
        [Roles.Finance] = new(StringComparer.Ordinal)
        {
            Perm.AuthLogin,
            Perm.InvoiceReadSelf, Perm.InvoiceReadAll, Perm.InvoiceSubmit,
            Perm.InvoiceReview, Perm.InvoiceUpdate,
            Perm.LogRead,
        },
        [Roles.Employee] = new(StringComparer.Ordinal)
        {
            Perm.AuthLogin,
            Perm.InvoiceReadSelf, Perm.InvoiceSubmit,
        },
    };

    //角色是否拥有该权限
    public static bool Can(string role, string permission)
        => Matrix.TryGetValue(role, out var set) && set.Contains(permission);

    //列出角色的全部权限
    public static IReadOnlyCollection<string> PermissionsOf(string role)
        => Matrix.TryGetValue(role, out var set) ? set : Array.Empty<string>();

    //人际约束

    //能否创建账号并设为该角色
    public static string? CanCreateAccount(string actorRole, string targetRole)
    {
        if (!Can(actorRole, Perm.AccountCreate)) return "当前角色无权创建账号";
        if (!Roles.IsValid(targetRole)) return "目标角色非法";
        if (targetRole == Roles.SuperAdmin) return "超级管理员账号只能由系统初始化，不能在界面上创建";
        if (targetRole == Roles.Admin && !Can(actorRole, Perm.AccountGrantAdmin))
            return "只有超级管理员可以创建管理员账号";
        return null;
    }

    //能否变更目标账号的角色
    public static string? CanChangeRole(string actorRole, string actorId, string targetId,
        string targetCurrentRole, string newRole, int superAdminCount)
    {
        if (!Roles.IsValid(newRole)) return "目标角色非法";
        if (actorId == targetId) return "不能修改自己的角色（避免自我提权）";
        if (newRole == Roles.SuperAdmin && targetCurrentRole != Roles.SuperAdmin)
            return "超级管理员角色只能由系统初始化，不能通过接口授予";

        var touchesAdmin = newRole == Roles.Admin || targetCurrentRole == Roles.Admin
                           || targetCurrentRole == Roles.SuperAdmin;
        if (touchesAdmin && !Can(actorRole, Perm.AccountGrantAdmin))
            return "只有超级管理员可以授予或撤销管理员/超级管理员";

        if (targetCurrentRole == Roles.SuperAdmin && newRole != Roles.SuperAdmin && superAdminCount <= 1)
            return "系统必须保留至少一个超级管理员";

        if (!Can(actorRole, Perm.AccountManage) && !Can(actorRole, Perm.AccountGrantAdmin))
            return "当前角色无权变更他人角色";
        return null;
    }

    //能否停用或启用账号
    public static string? CanSetDisabled(string actorRole, string actorId, string targetId,
        string targetRole, bool disable, int superAdminCount)
    {
        if (!Can(actorRole, Perm.AccountManage)) return "当前角色无权停用或启用账号";
        if (!disable) return null;
        if (actorId == targetId) return "不能停用自己的账号";
        if (targetRole == Roles.SuperAdmin && superAdminCount <= 1) return "系统必须保留至少一个超级管理员";
        return null;
    }

    //能否重置他人口令
    public static string? CanResetPassword(string actorRole, string actorId, string targetId, string targetRole)
    {
        if (actorId == targetId) return null;                 //改自己的口令走另一个接口
        if (!Can(actorRole, Perm.AccountManage)) return "当前角色无权重置他人口令";
        if (targetRole == Roles.SuperAdmin && actorRole != Roles.SuperAdmin)
            return "只有超级管理员可以重置超级管理员的口令";
        return null;
    }

    //能否删除账号
    public static string? CanDeleteAccount(string actorRole, string actorId, string targetId,
        string targetRole, int superAdminCount)
    {
        if (!Can(actorRole, Perm.AccountDelete)) return "只有超级管理员可以删除账号";
        if (actorId == targetId) return "不能删除自己的账号";
        if (targetRole == Roles.SuperAdmin && superAdminCount <= 1) return "系统必须保留至少一个超级管理员";
        return null;
    }
}
