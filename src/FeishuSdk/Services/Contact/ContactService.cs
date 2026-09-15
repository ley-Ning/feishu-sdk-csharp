using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Feishu.Services.Contact;

/// <summary>
/// 通讯录服务（contact/v3 高频子集：用户 CRUD、batch_get_id、部门查询），
/// 端点与 token 类型对齐 Go service/contact/v3。
/// </summary>
public sealed class ContactService
{
    internal ContactService(RequestPipeline pipeline)
    {
        User = new ContactUserResource(pipeline);
        Department = new ContactDepartmentResource(pipeline);
    }

    public ContactUserResource User { get; }

    public ContactDepartmentResource Department { get; }
}

internal static class ContactPaths
{
    public const string Users = "/open-apis/contact/v3/users";
    public const string UserItem = "/open-apis/contact/v3/users/:user_id";
    public const string UsersBatchGetId = "/open-apis/contact/v3/users/batch_get_id";
    public const string DepartmentItem = "/open-apis/contact/v3/departments/:department_id";
    public const string DepartmentChildren = "/open-apis/contact/v3/departments/:department_id/children";
}

// ==================== 用户 ====================

public sealed class ContactUserResource
{
    private readonly RequestPipeline _pipeline;

    internal ContactUserResource(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>查询单个用户信息（GET /open-apis/contact/v3/users/:user_id）。</summary>
    public Task<GetContactUserResponse> GetAsync(string userId, string? userIdType = null, string? departmentIdType = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = ContactPaths.UserItem,
            PathParams = { ["user_id"] = userId },
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (userIdType != null) api.QueryParams.Add("user_id_type", userIdType);
        if (departmentIdType != null) api.QueryParams.Add("department_id_type", departmentIdType);
        return _pipeline.SendForAsync<GetContactUserResponse>(api, options, cancellationToken);
    }

    /// <summary>创建用户（POST /open-apis/contact/v3/users）。</summary>
    public Task<CreateContactUserResponse> CreateAsync(CreateContactUserRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = ContactPaths.Users,
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        };
        if (request.UserIdType != null) api.QueryParams.Add("user_id_type", request.UserIdType);
        if (request.DepartmentIdType != null) api.QueryParams.Add("department_id_type", request.DepartmentIdType);
        return _pipeline.SendForAsync<CreateContactUserResponse>(api, options, cancellationToken);
    }

    /// <summary>修改用户部分信息（PATCH /open-apis/contact/v3/users/:user_id）。</summary>
    public Task<PatchContactUserResponse> PatchAsync(string userId, PatchContactUserRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Patch,
            Path = ContactPaths.UserItem,
            PathParams = { ["user_id"] = userId },
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        };
        if (request.UserIdType != null) api.QueryParams.Add("user_id_type", request.UserIdType);
        if (request.DepartmentIdType != null) api.QueryParams.Add("department_id_type", request.DepartmentIdType);
        return _pipeline.SendForAsync<PatchContactUserResponse>(api, options, cancellationToken);
    }

    /// <summary>删除用户（DELETE /open-apis/contact/v3/users/:user_id）。</summary>
    public Task<DeleteContactUserResponse> DeleteAsync(string userId, string? userIdType = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Delete,
            Path = ContactPaths.UserItem,
            PathParams = { ["user_id"] = userId },
            SupportedTokenTypes = [AccessTokenType.Tenant],
        };
        if (userIdType != null) api.QueryParams.Add("user_id_type", userIdType);
        return _pipeline.SendForAsync<DeleteContactUserResponse>(api, options, cancellationToken);
    }

    /// <summary>根据手机号或邮箱获取用户 ID（POST /open-apis/contact/v3/users/batch_get_id）。</summary>
    public Task<BatchGetUserIdResponse> BatchGetIdAsync(BatchGetUserIdRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = ContactPaths.UsersBatchGetId,
            Body = request.Body,
            SupportedTokenTypes = [AccessTokenType.Tenant],
        };
        if (request.UserIdType != null) api.QueryParams.Add("user_id_type", request.UserIdType);
        return _pipeline.SendForAsync<BatchGetUserIdResponse>(api, options, cancellationToken);
    }
}

public sealed class CreateContactUserRequest
{
    public string? UserIdType { get; set; }
    public string? DepartmentIdType { get; set; }
    public object? Body { get; set; }
}

public sealed record PatchContactUserRequest
{
    public string? UserIdType { get; init; }
    public string? DepartmentIdType { get; init; }
    public object? Body { get; init; }
}

public sealed record BatchGetUserIdRequest
{
    public string? UserIdType { get; init; }
    public BatchGetUserIdBody? Body { get; init; }
}

public sealed class BatchGetUserIdBody
{
    [JsonPropertyName("emails")]
    public List<string>? Emails { get; set; }

    [JsonPropertyName("mobiles")]
    public List<string>? Mobiles { get; set; }
}

public sealed class GetContactUserResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ContactUser? Data { get; set; }
}

public sealed class CreateContactUserResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ContactUser? Data { get; set; }
}

public sealed class PatchContactUserResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ContactUser? Data { get; set; }
}

public sealed class DeleteContactUserResponse : FeishuResponse;

public sealed class BatchGetUserIdResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public BatchGetUserIdResponseData? Data { get; set; }
}

public sealed class BatchGetUserIdResponseData
{
    [JsonPropertyName("user_list")]
    public List<BatchGetUserIdItem>? UserList { get; set; }
}

public sealed class BatchGetUserIdItem
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }
}

/// <summary>用户信息（常用字段子集；完整字段可用 client.PostAsync/DoAsync 直达）。</summary>
public sealed class ContactUser
{
    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("en_name")]
    public string? EnName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("mobile")]
    public string? Mobile { get; set; }

    [JsonPropertyName("gender")]
    public int? Gender { get; set; }

    [JsonPropertyName("avatar")]
    public Avatar? UserAvatar { get; set; }

    [JsonPropertyName("status")]
    public ContactUserStatus? Status { get; set; }

    [JsonPropertyName("department_ids")]
    public List<string>? DepartmentIds { get; set; }

    [JsonPropertyName("leader_user_id")]
    public string? LeaderUserId { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("work_place")]
    public string? WorkPlace { get; set; }

    [JsonPropertyName("join_time")]
    public long? JoinTime { get; set; }

    [JsonPropertyName("employee_no")]
    public string? EmployeeNo { get; set; }

    [JsonPropertyName("employee_type")]
    public int? EmployeeType { get; set; }
}

public sealed class Avatar
{
    [JsonPropertyName("avatar_72")]
    public string? Avatar72 { get; set; }

    [JsonPropertyName("avatar_240")]
    public string? Avatar240 { get; set; }

    [JsonPropertyName("avatar_640")]
    public string? Avatar640 { get; set; }

    [JsonPropertyName("avatar_origin")]
    public string? AvatarOrigin { get; set; }
}

public sealed class ContactUserStatus
{
    [JsonPropertyName("is_frozen")]
    public bool? IsFrozen { get; set; }

    [JsonPropertyName("is_resigned")]
    public bool? IsResigned { get; set; }

    [JsonPropertyName("is_activated")]
    public bool? IsActivated { get; set; }

    [JsonPropertyName("is_exited")]
    public bool? IsExited { get; set; }

    [JsonPropertyName("is_unjoin")]
    public bool? IsUnjoin { get; set; }
}

// ==================== 部门 ====================

public sealed class ContactDepartmentResource
{
    private readonly RequestPipeline _pipeline;

    internal ContactDepartmentResource(RequestPipeline pipeline) => _pipeline = pipeline;

    /// <summary>获取单个部门信息（GET /open-apis/contact/v3/departments/:department_id）。</summary>
    public Task<GetDepartmentResponse> GetAsync(string departmentId, string? userIdType = null, string? departmentIdType = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = ContactPaths.DepartmentItem,
            PathParams = { ["department_id"] = departmentId },
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (userIdType != null) api.QueryParams.Add("user_id_type", userIdType);
        if (departmentIdType != null) api.QueryParams.Add("department_id_type", departmentIdType);
        return _pipeline.SendForAsync<GetDepartmentResponse>(api, options, cancellationToken);
    }

    /// <summary>获取子部门列表（GET /open-apis/contact/v3/departments/:department_id/children，分页）。</summary>
    public Task<ListDepartmentChildrenResponse> ListChildrenAsync(string departmentId, ListDepartmentChildrenRequest? request = null, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var api = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = ContactPaths.DepartmentChildren,
            PathParams = { ["department_id"] = departmentId },
            SupportedTokenTypes = [AccessTokenType.Tenant, AccessTokenType.User],
        };
        if (request?.PageSize is { } pageSize) api.QueryParams.Add("page_size", pageSize.ToString());
        if (request?.PageToken is { Length: > 0 } token) api.QueryParams.Add("page_token", token);
        if (request?.FetchChild is { } fetchChild) api.QueryParams.Add("fetch_child", fetchChild ? "true" : "false");
        if (request?.UserIdType != null) api.QueryParams.Add("user_id_type", request.UserIdType);
        return _pipeline.SendForAsync<ListDepartmentChildrenResponse>(api, options, cancellationToken);
    }

    /// <summary>分页枚举子部门（自动翻页）。</summary>
    public async IAsyncEnumerable<ContactDepartment> EnumerateChildrenAsync(string departmentId, ListDepartmentChildrenRequest? request = null, RequestOptions? options = null, int? limit = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var emitted = 0;
        string? pageToken = null;
        do
        {
            var pageReq = pageToken == null
                ? request
                : (request ?? new ListDepartmentChildrenRequest()) with { PageToken = pageToken };
            var page = await ListChildrenAsync(departmentId, pageReq, options, cancellationToken);
            page.EnsureSuccess();
            foreach (var item in page.Data?.Items ?? [])
            {
                if (limit.HasValue && emitted >= limit.Value) yield break;
                emitted++;
                yield return item;
            }
            pageToken = page.Data?.PageToken is { Length: > 0 } ? page.Data.PageToken : null;
        } while (pageToken != null);
    }
}

public sealed record ListDepartmentChildrenRequest
{
    public int? PageSize { get; init; }
    public string? PageToken { get; init; }
    public bool? FetchChild { get; init; }
    public string? UserIdType { get; init; }
}

public sealed class GetDepartmentResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ContactDepartment? Data { get; set; }
}

public sealed class ListDepartmentChildrenResponse : FeishuResponse
{
    [JsonPropertyName("data")]
    public ListDepartmentChildrenResponseData? Data { get; set; }
}

public sealed class ListDepartmentChildrenResponseData
{
    [JsonPropertyName("items")]
    public List<ContactDepartment>? Items { get; set; }

    [JsonPropertyName("page_token")]
    public string? PageToken { get; set; }

    [JsonPropertyName("has_more")]
    public bool? HasMore { get; set; }
}

public sealed class ContactDepartment
{
    [JsonPropertyName("department_id")]
    public string? DepartmentId { get; set; }

    [JsonPropertyName("open_department_id")]
    public string? OpenDepartmentId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("i18n_name")]
    public string? I18nName { get; set; }

    [JsonPropertyName("parent_department_id")]
    public string? ParentDepartmentId { get; set; }

    [JsonPropertyName("leader_user_id")]
    public string? LeaderUserId { get; set; }

    [JsonPropertyName("member_count")]
    public int? MemberCount { get; set; }

    [JsonPropertyName("status")]
    public ContactDepartmentStatus? Status { get; set; }

    [JsonPropertyName("create_time")]
    public long? CreateTime { get; set; }

    [JsonPropertyName("modify_time")]
    public long? ModifyTime { get; set; }
}

public sealed class ContactDepartmentStatus
{
    [JsonPropertyName("is_deleted")]
    public bool? IsDeleted { get; set; }
}
