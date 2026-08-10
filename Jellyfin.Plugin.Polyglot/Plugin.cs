using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Polyglot.Configuration;
using Jellyfin.Plugin.Polyglot.Helpers;
using Jellyfin.Plugin.Polyglot.Services;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Polyglot;

/// <summary>
/// Polyglot Plugin for Jellyfin.
/// Enables multi-language metadata support through library mirroring.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string SidebarLinkName = "Polyglot";

    private readonly IApplicationHost _applicationHost;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="applicationHost">The Jellyfin application host for resolving services.</param>
    /// <param name="logger">The plugin logger.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        IApplicationHost applicationHost,
        ILogger<Plugin> logger)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _applicationHost = applicationHost;
        _applicationPaths = applicationPaths;
        _logger = logger;

        EnsureSidebarMenuLink();
    }

    /// <inheritdoc />
    public override string Name => "Polyglot";

    /// <inheritdoc />
    public override string Description => "Multi-language metadata support through library mirroring with hardlinks.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");

    /// <summary>
    /// Gets the plugin configuration.
    /// </summary>
    public PluginConfiguration PluginConfiguration => Configuration;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }

    /// <inheritdoc />
    public override void OnUninstalling()
    {
        _logger.PolyglotInfo("Plugin OnUninstalling: Starting cleanup");

        RemoveSidebarMenuLink();

        try
        {
            // Resolve services for cleanup
            var mirrorService = _applicationHost.Resolve<IMirrorService>();
            var configService = _applicationHost.Resolve<IConfigurationService>();

            // Get mirror IDs to iterate
            var mirrorIds = configService.Read(c => c.LanguageAlternatives
                .SelectMany(a => a.MirroredLibraries.Select(m => new { m.Id, m.TargetLibraryName }))
                .ToList());

            _logger.PolyglotInfo("Plugin OnUninstalling: Deleting {0} mirrors", mirrorIds.Count);

            foreach (var mirror in mirrorIds)
            {
                var mirrorEntity = configService.CreateLogMirror(mirror.Id);
                try
                {
                    _logger.PolyglotDebug("Plugin OnUninstalling: Deleting mirror {0}", mirrorEntity);

                    // Use forceConfigRemoval=true during uninstall to ensure cleanup completes
                    var result = mirrorService
                        .DeleteMirrorAsync(mirror.Id, deleteLibrary: true, deleteFiles: true, forceConfigRemoval: true)
                        .GetAwaiter()
                        .GetResult();

                    if (result.HasErrors)
                    {
                        _logger.PolyglotWarning("Plugin OnUninstalling: Mirror {0} removed with errors: {1} {2}",
                            mirrorEntity, result.LibraryDeletionError, result.FileDeletionError);
                    }
                }
                catch (Exception ex)
                {
                    _logger.PolyglotWarning(ex, "Plugin OnUninstalling: Failed to delete mirror {0}", mirrorEntity);
                }
            }

            // Clear all configuration
            configService.Update(c =>
            {
                c.LanguageAlternatives.Clear();
                c.UserLanguages.Clear();
            });

            _logger.PolyglotInfo("Plugin OnUninstalling: Cleanup completed");
        }
        catch (Exception ex)
        {
            _logger.PolyglotError(ex, "Plugin OnUninstalling: Unexpected error during cleanup");
        }
    }

    /// <summary>
    /// Adds a "Polyglot" entry to the web client's sidebar menu by editing config.json,
    /// using Jellyfin's built-in menuLinks feature (available since 10.8). This is idempotent;
    /// it does nothing if the link is already present.
    /// </summary>
    private void EnsureSidebarMenuLink()
    {
        try
        {
            var webConfigPath = Path.Combine(_applicationPaths.WebPath, "config.json");

            if (!File.Exists(webConfigPath))
            {
                _logger.PolyglotWarning(
                    "EnsureSidebarMenuLink: config.json not found at {0}; server may not be hosting static web content, skipping",
                    webConfigPath);
                return;
            }

            var json = File.ReadAllText(webConfigPath);
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                _logger.PolyglotWarning("EnsureSidebarMenuLink: Unable to parse config.json, skipping");
                return;
            }

            if (root["menuLinks"] is not JsonArray menuLinks)
            {
                menuLinks = new JsonArray();
                root["menuLinks"] = menuLinks;
            }

            var alreadyPresent = menuLinks.Any(node =>
                node is JsonObject obj &&
                string.Equals(obj["name"]?.GetValue<string>(), SidebarLinkName, StringComparison.Ordinal));

            if (alreadyPresent)
            {
                return;
            }

            menuLinks.Add(new JsonObject
            {
                ["name"] = SidebarLinkName,
                ["icon"] = "translate",
                ["url"] = "/web/#/configurationpage?name=Polyglot"
            });

            File.WriteAllText(webConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            _logger.PolyglotInfo("EnsureSidebarMenuLink: Added Polyglot sidebar link to config.json");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.PolyglotWarning(
                ex,
                "EnsureSidebarMenuLink: Permission denied writing to config.json. If running in Docker, the web " +
                "files may be owned by a different user than the Jellyfin process; the sidebar link will need to " +
                "be added manually.");
        }
        catch (Exception ex)
        {
            _logger.PolyglotWarning(ex, "EnsureSidebarMenuLink: Failed to add sidebar menu link");
        }
    }

    /// <summary>
    /// Removes the "Polyglot" sidebar menu link from config.json, run during plugin uninstall
    /// to avoid leaving a dead link behind.
    /// </summary>
    private void RemoveSidebarMenuLink()
    {
        try
        {
            var webConfigPath = Path.Combine(_applicationPaths.WebPath, "config.json");

            if (!File.Exists(webConfigPath))
            {
                return;
            }

            var json = File.ReadAllText(webConfigPath);
            if (JsonNode.Parse(json) is not JsonObject root || root["menuLinks"] is not JsonArray menuLinks)
            {
                return;
            }

            var removed = false;
            for (var i = menuLinks.Count - 1; i >= 0; i--)
            {
                if (menuLinks[i] is JsonObject obj &&
                    string.Equals(obj["name"]?.GetValue<string>(), SidebarLinkName, StringComparison.Ordinal))
                {
                    menuLinks.RemoveAt(i);
                    removed = true;
                }
            }

            if (!removed)
            {
                return;
            }

            File.WriteAllText(webConfigPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            _logger.PolyglotInfo("RemoveSidebarMenuLink: Removed Polyglot sidebar link from config.json");
        }
        catch (Exception ex)
        {
            _logger.PolyglotWarning(ex, "RemoveSidebarMenuLink: Failed to remove sidebar menu link during uninstall");
        }
    }
}
