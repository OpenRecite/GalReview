public static class GamePackageEndpoints
{
    public static void MapGamePackageEndpoints(
        this WebApplication app,
        string gatewayKey,
        IReadOnlySet<string> packageReaderAllowedServices,
        IReadOnlySet<string> validationAllowedServices)
    {
        app.MapGet("/api/v1/game-packages/{packageId}", (string packageId, HttpContext c, IGameStore store) =>
        {
            var userId = GalGameHttp.GatewayUser(c, gatewayKey);
            if (userId is null) return GalGameHttp.Failure(c, 401, "AUTH_REQUIRED", "需要网关认证的用户身份。");
            if (!GalGameHttp.TryParseUuidV4(packageId, out var id))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "packageId 必须为 UUID v4。");
            var manifest = store.GetManifest(id);
            if (manifest is null || manifest.OwnerUserId != userId)
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "游戏包不存在。");
            return Results.Ok(ApiSuccess.Create(manifest, c.TraceIdentifier));
        });

        app.MapGet("/api/v1/game-packages/{packageId}/content", (string packageId, HttpContext c, IGameStore store) =>
        {
            var userId = GalGameHttp.GatewayUser(c, gatewayKey);
            if (userId is null) return GalGameHttp.Failure(c, 401, "AUTH_REQUIRED", "需要网关认证的用户身份。");
            if (!GalGameHttp.TryParseUuidV4(packageId, out var id))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "packageId 必须为 UUID v4。");

            var owner = store.GetPackageOwner(id);
            if (owner is null || owner != userId)
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "游戏包不存在。");

            var manifest = store.GetManifest(id);
            var package = store.GetPackage(id);
            if (manifest is null || package is null)
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "游戏包不存在。");

            var etag = $"\"{manifest.Checksum}\"";
            c.Response.Headers.ETag = etag;
            c.Response.Headers.CacheControl = "private, no-cache";
            c.Response.Headers.XContentTypeOptions = "nosniff";
            if (GalGameHttp.IfNoneMatchMatches(c.Request.Headers.IfNoneMatch, etag))
                return Results.StatusCode(304);

            return Results.Text(
                GamePackageValidator.SerializeCanonical(package),
                "application/json; charset=utf-8");
        });

        app.MapGet("/api/v1/game-packages/{packageId}/audio/{assetId}", (
            string packageId,
            string assetId,
            HttpContext c,
            IGameStore store) =>
        {
            var userId = GalGameHttp.GatewayUser(c, gatewayKey);
            if (userId is null)
                return GalGameHttp.Failure(c, 401, "AUTH_REQUIRED", "需要网关认证的用户身份。");
            if (!GalGameHttp.TryParseUuidV4(packageId, out var id)
                || string.IsNullOrWhiteSpace(assetId)
                || assetId.Length > 128)
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "packageId 或 assetId 格式无效。");

            var owner = store.GetPackageOwner(id);
            var package = store.GetPackage(id);
            if (owner is null || package is null || owner != userId)
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "语音资源不存在。");

            var expectedUri = $"/api/v1/game-packages/{id}/audio/{assetId}";
            var referenced = (package.Assets ?? Array.Empty<AssetRef>()).Any(asset =>
                asset is not null
                && asset.Type == AssetType.AUDIO
                && string.Equals(asset.AssetId, assetId, StringComparison.Ordinal)
                && string.Equals(asset.Uri, expectedUri, StringComparison.Ordinal));
            var audio = referenced ? store.GetAudio(id, assetId) : null;
            if (audio is null)
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "语音资源不存在。");

            c.Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            c.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(audio.Data, audio.ContentType, enableRangeProcessing: true);
        });

        app.MapGet("/internal/v1/game-packages/{packageId}", (
            string packageId,
            string? ownerUserId,
            HttpContext c,
            IGameStore store) =>
        {
            if (!InternalServiceAccessPolicy.IsTrusted(
                    c.Request.Headers, gatewayKey, packageReaderAllowedServices))
                return GalGameHttp.Failure(c, 403, "FORBIDDEN", "需要经 Gateway 转发的可信 RenderService 身份。");

            if (!GalGameHttp.TryParseUuidV4(packageId, out var id)
                || !GalGameHttp.TryParseUuidV4(ownerUserId, out _))
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "packageId 与 ownerUserId 必须为 UUID v4。");

            var owner = store.GetPackageOwner(id);
            var package = store.GetPackage(id);
            if (owner is null || package is null
                || !string.Equals(owner, ownerUserId, StringComparison.OrdinalIgnoreCase))
                return GalGameHttp.Failure(c, 404, "RESOURCE_NOT_FOUND", "游戏包不存在。");

            return Results.Ok(ApiSuccess.Create(package, c.TraceIdentifier));
        });

        app.MapPost("/internal/v1/game-package-validations", (GamePackageValidationRequest request, HttpContext c, GamePackageValidator validator) =>
        {
            if (!InternalServiceAccessPolicy.IsTrusted(
                    c.Request.Headers, gatewayKey, validationAllowedServices))
                return GalGameHttp.Failure(c, 403, "FORBIDDEN", "需要经 Gateway 转发的可信服务身份。");

            if (request.Package is null)
                return GalGameHttp.Failure(c, 400, "VALIDATION_ERROR", "package 不能为空。");

            var result = validator.Validate(request.Package);
            return Results.Json(
                ApiSuccess.Create(result, c.TraceIdentifier),
                statusCode: result.Valid ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
        });
    }
}
