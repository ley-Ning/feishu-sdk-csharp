using Feishu.Services.Im;

namespace Feishu;

/// <summary>
/// SDK 主入口（对应 Go 版 <c>lark.Client</c>）。线程安全，建议进程内单例。
/// </summary>
/// <example>
/// <code>
/// var client = new FeishuClient(new FeishuOptions
/// {
///     AppId = Environment.GetEnvironmentVariable("FEISHU_APP_ID")!,
///     AppSecret = Environment.GetEnvironmentVariable("FEISHU_APP_SECRET")!,
/// });
/// var resp = await client.Im.Message.CreateAsync(new SendMessageRequest
/// {
///     ReceiveIdType = "open_id",
///     Body = new SendMessageBody
///     {
///         ReceiveId = "ou_xxx",
///         MsgType = "text",
///         Content = """{"text":"hello"}""",
///     },
/// });
/// </code>
/// </example>
public sealed class FeishuClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public FeishuOptions Options { get; }

    internal RequestPipeline Pipeline { get; }

    /// <summary>IM（消息与群组）服务。</summary>
    public ImService Im { get; }

    /// <summary>通讯录（用户 / 部门）服务。</summary>
    public Services.Contact.ContactService Contact { get; }

    /// <summary>用户身份认证（authen v1）服务。</summary>
    public Services.Authen.AuthenService Authen { get; }

    /// <summary>多维表格服务（生成代码）。</summary>
    public Services.Bitable.BitableService Bitable { get; }

    /// <summary>云空间服务（生成代码）。</summary>
    public Services.Drive.DriveService Drive { get; }

    /// <summary>审批服务（生成代码）。</summary>
    public Services.Approval.ApprovalService Approval { get; }

    /// <summary>任务服务（生成代码）。</summary>
    public Services.Task.TaskService Task { get; }

    /// <summary>文档服务（生成代码）。</summary>
    public Services.Docx.DocxService Docx { get; }

    /// <summary>电子表格服务（生成代码）。</summary>
    public Services.Sheets.SheetsService Sheets { get; }

    /// <summary>日历服务（生成代码）。</summary>
    public Services.Calendar.CalendarService Calendar { get; }

    /// <summary>知识库服务（生成代码）。</summary>
    public Services.Wiki.WikiService Wiki { get; }

    /// <summary>搜索服务（生成代码）。</summary>
    public Services.Search.SearchService Search { get; }

    /// <summary>扩展服务（drive explorer / authen 便捷封装）。</summary>
    public Services.Ext.ExtService Ext { get; }

    /// <summary>白板服务（生成代码）。</summary>
    public Services.Board.BoardService Board { get; }

    /// <summary>邮箱服务（生成代码）。</summary>
    public Services.Mail.MailService Mail { get; }

    /// <summary>旧版文档服务（生成代码）。</summary>
    public Services.Docs.DocsService Docs { get; }

    /// <summary>租户信息服务（生成代码）。</summary>
    public Services.Tenant.TenantService Tenant { get; }

    /// <summary>机器人信息服务（生成代码）。</summary>
    public Services.Bot.BotService Bot { get; }

    /// <summary>应用信息服务（生成代码）。</summary>
    public Services.Application.ApplicationService Application { get; }

    /// <summary>机器翻译服务（生成代码）。</summary>
    public Services.Translation.TranslationService Translation { get; }

    /// <summary>文字识别服务（生成代码）。</summary>
    public Services.Ocr.OcrService Ocr { get; }

    /// <summary>卡片实体服务（生成代码，卡片 JSON 2.0）。</summary>
    public Services.Cardkit.CardkitService Cardkit { get; }

    /// <summary>朋友圈服务（生成代码）。</summary>
    public Services.Moments.MomentsService Moments { get; }

    /// <summary>企业认证信息服务（生成代码）。</summary>
    public Services.Verification.VerificationService Verification { get; }

    /// <summary>智能门禁服务（生成代码）。</summary>
    public Services.Acs.AcsService Acs { get; }

    /// <summary>考勤服务（生成代码，36 端点）。</summary>
    public Services.Attendance.AttendanceService Attendance { get; }

    /// <summary>服务台服务（生成代码，44 端点，工单接口自动携带服务台鉴权头）。</summary>
    public Services.Helpdesk.HelpdeskService Helpdesk { get; }

    /// <summary>妙记服务（生成代码）。</summary>
    public Services.Minutes.MinutesService Minutes { get; }

    /// <summary>事件订阅管理服务（生成代码）。</summary>
    public Services.EventService.EventServiceService EventSvc { get; }

    /// <summary>OKR 服务（生成代码）。</summary>
    public Services.Okr.OkrService Okr { get; }

    /// <summary>视频会议服务（生成代码）。</summary>
    public Services.Vc.VcService Vc { get; }

    /// <summary>企业管理服务（生成代码）。</summary>
    public Services.Admin.AdminService Admin { get; }

    /// <summary>多维表格高级权限服务（生成代码）。</summary>
    public Services.Base.BaseService Base { get; }

    /// <summary>小黑板 Block 服务（生成代码）。</summary>
    public Services.Block.BlockService Block { get; }

    /// <summary>汇报服务（生成代码）。</summary>
    public Services.Report.ReportService Report { get; }

    /// <summary>个人设置服务（生成代码）。</summary>
    public Services.PersonalSettings.PersonalSettingsService PersonalSettings { get; }

    /// <summary>通讯录企业版服务（生成代码）。</summary>
    public Services.Directory.DirectoryService Directory { get; }

    /// <summary>飞书词典服务（生成代码）。</summary>
    public Services.Lingo.LingoService Lingo { get; }

    /// <summary>信任组织服务（生成代码）。</summary>
    public Services.TrustParty.TrustPartyService TrustParty { get; }

    /// <summary>文档 AI 识别服务（生成代码，18 类证照票据）。</summary>
    public Services.DocumentAi.DocumentAiService DocumentAi { get; }

    /// <summary>语音转文字服务（生成代码 ASR）。</summary>
    public Services.SpeechToText.SpeechToTextService SpeechToText { get; }

    /// <summary>实名认证服务（生成代码）。</summary>
    public Services.HumanAuthentication.HumanAuthenticationService HumanAuthentication { get; }

    /// <summary>账密安全服务（生成代码）。</summary>
    public Services.Passport.PassportService Passport { get; }

    /// <summary>妙搭低代码服务（生成代码，User-only）。</summary>
    public Services.Spark.SparkService Spark { get; }

    /// <summary>工作台数据服务（生成代码）。</summary>
    public Services.Workplace.WorkplaceService Workplace { get; }

    /// <summary>主数据管理服务（生成代码，v1+v3）。</summary>
    public Services.Mdm.MdmService Mdm { get; }

    /// <summary>薪酬服务（生成代码）。</summary>
    public Services.Compensation.CompensationService Compensation { get; }

    /// <summary>算薪服务（生成代码）。</summary>
    public Services.Payroll.PayrollService Payroll { get; }

    /// <summary>绩效服务（生成代码）。</summary>
    public Services.Performance.PerformanceService Performance { get; }

    /// <summary>统一密钥管理服务（生成代码）。</summary>
    public Services.UnifiedKms.UnifiedKmsService UnifiedKms { get; }

    /// <summary>智能伙伴服务（生成代码）。</summary>
    public Services.Aily.AilyService Aily { get; }

    /// <summary>安全合规服务（生成代码，设备管理）。</summary>
    public Services.SecurityAndCompliance.SecurityAndComplianceService SecurityAndCompliance { get; }

    /// <summary>低代码平台服务（生成代码）。</summary>
    public Services.Apaas.ApaasService Apaas { get; }

    /// <summary>电子劳动合同服务（生成代码）。</summary>
    public Services.Ehr.EhrService Ehr { get; }

    /// <summary>招聘服务（生成代码，已录前 8 资源）。</summary>
    public Services.Hire.HireService Hire { get; }

    /// <summary>飞书人事企业版服务（生成代码，corehr/v1 已录前 11 资源）。</summary>
    public Services.Corehr.CorehrService Corehr { get; }

    /// <summary>飞书人事企业版 v2 服务（生成代码，已录前 16 资源）。</summary>
    public Services.CorehrV2.CorehrV2Service CorehrV2 { get; }

    /// <summary>OAuth 用户访问令牌（授权码换取 / 刷新）。</summary>
    public Auth.OAuthTokenService OAuth { get; }

    public FeishuClient(FeishuOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Options = options;

        if (options.HttpMessageHandlerFactory != null)
        {
            _httpClient = new HttpClient(options.HttpMessageHandlerFactory(), disposeHandler: true);
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }
        if (options.RequestTimeout > TimeSpan.Zero)
            _httpClient.Timeout = options.RequestTimeout;

        Pipeline = new RequestPipeline(options, _httpClient);
        Im = new ImService(Pipeline);
        Contact = new Services.Contact.ContactService(Pipeline);
        Authen = new Services.Authen.AuthenService(Pipeline);
        Bitable = new Services.Bitable.BitableService(Pipeline);
        Drive = new Services.Drive.DriveService(Pipeline);
        Approval = new Services.Approval.ApprovalService(Pipeline);
        Task = new Services.Task.TaskService(Pipeline);
        Docx = new Services.Docx.DocxService(Pipeline);
        Sheets = new Services.Sheets.SheetsService(Pipeline);
        Calendar = new Services.Calendar.CalendarService(Pipeline);
        Wiki = new Services.Wiki.WikiService(Pipeline);
        Search = new Services.Search.SearchService(Pipeline);
        Ext = new Services.Ext.ExtService(Pipeline);
        Board = new Services.Board.BoardService(Pipeline);
        Mail = new Services.Mail.MailService(Pipeline);
        Docs = new Services.Docs.DocsService(Pipeline);
        Tenant = new Services.Tenant.TenantService(Pipeline);
        Bot = new Services.Bot.BotService(Pipeline);
        Application = new Services.Application.ApplicationService(Pipeline);
        Translation = new Services.Translation.TranslationService(Pipeline);
        Ocr = new Services.Ocr.OcrService(Pipeline);
        Cardkit = new Services.Cardkit.CardkitService(Pipeline);
        Moments = new Services.Moments.MomentsService(Pipeline);
        Verification = new Services.Verification.VerificationService(Pipeline);
        Acs = new Services.Acs.AcsService(Pipeline);
        Attendance = new Services.Attendance.AttendanceService(Pipeline);
        Helpdesk = new Services.Helpdesk.HelpdeskService(Pipeline);
        Minutes = new Services.Minutes.MinutesService(Pipeline);
        EventSvc = new Services.EventService.EventServiceService(Pipeline);
        Okr = new Services.Okr.OkrService(Pipeline);
        Vc = new Services.Vc.VcService(Pipeline);
        Admin = new Services.Admin.AdminService(Pipeline);
        Base = new Services.Base.BaseService(Pipeline);
        Block = new Services.Block.BlockService(Pipeline);
        Report = new Services.Report.ReportService(Pipeline);
        PersonalSettings = new Services.PersonalSettings.PersonalSettingsService(Pipeline);
        Directory = new Services.Directory.DirectoryService(Pipeline);
        Lingo = new Services.Lingo.LingoService(Pipeline);
        TrustParty = new Services.TrustParty.TrustPartyService(Pipeline);
        DocumentAi = new Services.DocumentAi.DocumentAiService(Pipeline);
        SpeechToText = new Services.SpeechToText.SpeechToTextService(Pipeline);
        HumanAuthentication = new Services.HumanAuthentication.HumanAuthenticationService(Pipeline);
        Passport = new Services.Passport.PassportService(Pipeline);
        Spark = new Services.Spark.SparkService(Pipeline);
        Workplace = new Services.Workplace.WorkplaceService(Pipeline);
        Mdm = new Services.Mdm.MdmService(Pipeline);
        Compensation = new Services.Compensation.CompensationService(Pipeline);
        Payroll = new Services.Payroll.PayrollService(Pipeline);
        Performance = new Services.Performance.PerformanceService(Pipeline);
        UnifiedKms = new Services.UnifiedKms.UnifiedKmsService(Pipeline);
        Aily = new Services.Aily.AilyService(Pipeline);
        SecurityAndCompliance = new Services.SecurityAndCompliance.SecurityAndComplianceService(Pipeline);
        Apaas = new Services.Apaas.ApaasService(Pipeline);
        Ehr = new Services.Ehr.EhrService(Pipeline);
        Hire = new Services.Hire.HireService(Pipeline);
        Corehr = new Services.Corehr.CorehrService(Pipeline);
        CorehrV2 = new Services.CorehrV2.CorehrV2Service(Pipeline);
        OAuth = new Auth.OAuthTokenService(Pipeline);

        // 商店应用启动时主动触发一次 app_ticket 重推（对齐 Go 版行为）
        if (options.AppType == FeishuAppType.Marketplace && options.ClientAssertionProvider == null)
            _ = Pipeline.AppTickets.ResendAppTicketAsync();

        options.Logger.Info("feishu client ready");
    }

    /// <summary>复用外部 HttpClient（IHttpClientFactory 场景）。</summary>
    public FeishuClient(FeishuOptions options, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(httpClient);
        Options = options;
        _httpClient = httpClient;
        _ownsHttpClient = false;
        Pipeline = new RequestPipeline(options, _httpClient);
        Im = new ImService(Pipeline);
        Contact = new Services.Contact.ContactService(Pipeline);
        Authen = new Services.Authen.AuthenService(Pipeline);
        Bitable = new Services.Bitable.BitableService(Pipeline);
        Drive = new Services.Drive.DriveService(Pipeline);
        Approval = new Services.Approval.ApprovalService(Pipeline);
        Task = new Services.Task.TaskService(Pipeline);
        Docx = new Services.Docx.DocxService(Pipeline);
        Sheets = new Services.Sheets.SheetsService(Pipeline);
        Calendar = new Services.Calendar.CalendarService(Pipeline);
        Wiki = new Services.Wiki.WikiService(Pipeline);
        Search = new Services.Search.SearchService(Pipeline);
        Ext = new Services.Ext.ExtService(Pipeline);
        Board = new Services.Board.BoardService(Pipeline);
        Mail = new Services.Mail.MailService(Pipeline);
        Docs = new Services.Docs.DocsService(Pipeline);
        Tenant = new Services.Tenant.TenantService(Pipeline);
        Bot = new Services.Bot.BotService(Pipeline);
        Application = new Services.Application.ApplicationService(Pipeline);
        Translation = new Services.Translation.TranslationService(Pipeline);
        Ocr = new Services.Ocr.OcrService(Pipeline);
        Cardkit = new Services.Cardkit.CardkitService(Pipeline);
        Moments = new Services.Moments.MomentsService(Pipeline);
        Verification = new Services.Verification.VerificationService(Pipeline);
        Acs = new Services.Acs.AcsService(Pipeline);
        Attendance = new Services.Attendance.AttendanceService(Pipeline);
        Helpdesk = new Services.Helpdesk.HelpdeskService(Pipeline);
        Minutes = new Services.Minutes.MinutesService(Pipeline);
        EventSvc = new Services.EventService.EventServiceService(Pipeline);
        Okr = new Services.Okr.OkrService(Pipeline);
        Vc = new Services.Vc.VcService(Pipeline);
        Admin = new Services.Admin.AdminService(Pipeline);
        Base = new Services.Base.BaseService(Pipeline);
        Block = new Services.Block.BlockService(Pipeline);
        Report = new Services.Report.ReportService(Pipeline);
        PersonalSettings = new Services.PersonalSettings.PersonalSettingsService(Pipeline);
        Directory = new Services.Directory.DirectoryService(Pipeline);
        Lingo = new Services.Lingo.LingoService(Pipeline);
        TrustParty = new Services.TrustParty.TrustPartyService(Pipeline);
        DocumentAi = new Services.DocumentAi.DocumentAiService(Pipeline);
        SpeechToText = new Services.SpeechToText.SpeechToTextService(Pipeline);
        HumanAuthentication = new Services.HumanAuthentication.HumanAuthenticationService(Pipeline);
        Passport = new Services.Passport.PassportService(Pipeline);
        Spark = new Services.Spark.SparkService(Pipeline);
        Workplace = new Services.Workplace.WorkplaceService(Pipeline);
        Mdm = new Services.Mdm.MdmService(Pipeline);
        Compensation = new Services.Compensation.CompensationService(Pipeline);
        Payroll = new Services.Payroll.PayrollService(Pipeline);
        Performance = new Services.Performance.PerformanceService(Pipeline);
        UnifiedKms = new Services.UnifiedKms.UnifiedKmsService(Pipeline);
        Aily = new Services.Aily.AilyService(Pipeline);
        SecurityAndCompliance = new Services.SecurityAndCompliance.SecurityAndComplianceService(Pipeline);
        Apaas = new Services.Apaas.ApaasService(Pipeline);
        Ehr = new Services.Ehr.EhrService(Pipeline);
        Hire = new Services.Hire.HireService(Pipeline);
        Corehr = new Services.Corehr.CorehrService(Pipeline);
        CorehrV2 = new Services.CorehrV2.CorehrV2Service(Pipeline);
        OAuth = new Auth.OAuthTokenService(Pipeline);
        options.Logger.Info("feishu client ready");
    }

    // ---- 原始 API 调用（未覆盖的端点直接用这组方法） ----

    public Task<ApiResponse> PostAsync(string path, object? body, AccessTokenType tokenType = AccessTokenType.Tenant, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = path,
            Body = body,
            SupportedTokenTypes = [tokenType],
        }, options, cancellationToken);

    public Task<ApiResponse> GetAsync(string path, object? body = null, AccessTokenType tokenType = AccessTokenType.Tenant, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = path,
            Body = body,
            SupportedTokenTypes = [tokenType],
        }, options, cancellationToken);

    public Task<ApiResponse> PutAsync(string path, object? body, AccessTokenType tokenType = AccessTokenType.Tenant, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Put,
            Path = path,
            Body = body,
            SupportedTokenTypes = [tokenType],
        }, options, cancellationToken);

    public Task<ApiResponse> PatchAsync(string path, object? body, AccessTokenType tokenType = AccessTokenType.Tenant, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Patch,
            Path = path,
            Body = body,
            SupportedTokenTypes = [tokenType],
        }, options, cancellationToken);

    public Task<ApiResponse> DeleteAsync(string path, object? body = null, AccessTokenType tokenType = AccessTokenType.Tenant, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Pipeline.SendAsync(new ApiRequest
        {
            Method = HttpMethod.Delete,
            Path = path,
            Body = body,
            SupportedTokenTypes = [tokenType],
        }, options, cancellationToken);

    public Task<ApiResponse> DoAsync(ApiRequest request, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Pipeline.SendAsync(request, options, cancellationToken);

    // ---- token 手动获取（禁用缓存托管时自行换取） ----

    public Task<string> GetAppAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Pipeline.Tokens.GetAppAccessTokenAsync(cancellationToken: cancellationToken);

    public Task<string> GetTenantAccessTokenAsync(string? tenantKey = null, CancellationToken cancellationToken = default) =>
        Pipeline.Tokens.GetTenantAccessTokenAsync(tenantKey, cancellationToken: cancellationToken);

    /// <summary>请求飞书重推 app_ticket（对齐 Go client.ResendAppTicket）。</summary>
    public Task<bool> ResendAppTicketAsync(CancellationToken cancellationToken = default) =>
        Pipeline.AppTickets.ResendAppTicketAsync(cancellationToken);

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }
}
