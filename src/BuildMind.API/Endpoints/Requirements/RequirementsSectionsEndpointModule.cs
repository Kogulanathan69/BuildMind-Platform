using System.Text.Json;
using BuildMind.API.Errors;
using BuildMind.API.Routing;
using BuildMind.Application.Features.Requirements.Abstractions;
using BuildMind.Application.Features.Requirements.Contracts;

namespace BuildMind.API.Endpoints.Requirements;

/// <summary>Provisional isolated Day 04 routes; production activation remains prohibited.</summary>
public sealed class RequirementsSectionsEndpointModule : IApiEndpointModule
{
    public void MapEndpoints(RouteGroupBuilder apiV1)
    {
        ArgumentNullException.ThrowIfNull(apiV1);
        var group = apiV1.MapGroup("/projects/{id}/requirements").WithTags("Requirements").RequireAuthorization();
        group.MapGet("/spaces", GetAsync).WithName("GetRequirementSpaces");
        group.MapGet("/relationships", GetAsync).WithName("GetRequirementRelationships");
        group.MapPut("/spaces", PutSpacesAsync).WithName("ReplaceRequirementSpaces");
        group.MapPut("/relationships", PutRelationshipsAsync).WithName("ReplaceRequirementRelationships");
    }

    private static async Task<IResult> GetAsync(string id, HttpContext context, IRequirementsSectionApplicationService service, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(id, out var project) || project == Guid.Empty) return RequirementsEndpointModule.InvalidProject();
        return RequirementsEndpointModule.Map(await service.GetAsync(project, cancellationToken), project);
    }

    private static Task<IResult> PutSpacesAsync(string id, HttpContext context, IRequirementsSectionApplicationService service, CancellationToken token) =>
        PutAsync<PutAdditionalSpacesHttpRequest>(id, context,
            (project, request, cancellation) => service.SaveSpacesAsync(project, request.ToApplicationRequest(), cancellation), token);

    private static Task<IResult> PutRelationshipsAsync(string id, HttpContext context, IRequirementsSectionApplicationService service, CancellationToken token) =>
        PutAsync<PutRoomRelationshipsHttpRequest>(id, context,
            (project, request, cancellation) => service.SaveRelationshipsAsync(project, request.ToApplicationRequest(), cancellation), token);

    private static async Task<IResult> PutAsync<T>(string id, HttpContext context,
        Func<Guid, T, CancellationToken, Task<RequirementsOperationResult>> save, CancellationToken token) where T : PutRequirementsSectionHttpRequest
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(id, out var project) || project == Guid.Empty) return RequirementsEndpointModule.InvalidProject();
        if (!context.Request.HasJsonContentType()) return RequirementsEndpointModule.Problem(415);
        T? request;
        try { request = await context.Request.ReadFromJsonAsync<T>(token); }
        catch (JsonException) { return InvalidBody(); }
        if (request is null) return InvalidBody();
        var errors = request.GetStructuralErrors();
        if (errors.Count > 0) return ApiProblems.ValidationFailed(errors);
        return RequirementsEndpointModule.Map(await save(project, request, token), project, save: true);
    }

    private static IResult InvalidBody() => ApiProblems.ValidationFailed(
        new Dictionary<string, string[]> { ["request"] = ["An explicitly supplied valid section snapshot is required."] });
}
