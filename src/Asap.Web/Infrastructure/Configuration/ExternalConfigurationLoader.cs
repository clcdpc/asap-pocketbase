using Microsoft.Extensions.Configuration;

namespace Asap.Web.Infrastructure.Configuration;

public static class ExternalConfigurationLoader
{
    public static ConfigurationLoadResult Load(
        IConfiguration bootstrapConfiguration,
        string contentRootPath,
        bool allowFileWithinContentRoot)
    {
        var configuredPath = bootstrapConfiguration["Asap:ConfigFile"];
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return ConfigurationLoadResult.Invalid("config_file_not_configured");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(
                Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(contentRootPath, configuredPath));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ConfigurationLoadResult.Invalid("config_file_path_invalid");
        }

        if (!allowFileWithinContentRoot && IsWithin(fullPath, contentRootPath))
        {
            return new ConfigurationLoadResult(
                null,
                fullPath,
                ["config_file_must_be_external"]);
        }

        try
        {
            var externalRoot = new ConfigurationBuilder()
                .SetBasePath(Path.GetDirectoryName(fullPath)!)
                .AddJsonFile(Path.GetFileName(fullPath), optional: false, reloadOnChange: false)
                .Build();

            var value = new ExternalConfiguration();
            externalRoot.Bind(value);
            var errors = ExternalConfigurationValidator.Validate(value);

            return new ConfigurationLoadResult(
                errors.Count == 0 ? value : null,
                fullPath,
                errors);
        }
        catch (FileNotFoundException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_not_found"]);
        }
        catch (DirectoryNotFoundException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_not_found"]);
        }
        catch (InvalidDataException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_json_invalid"]);
        }
        catch (FormatException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_json_invalid"]);
        }
        catch (UnauthorizedAccessException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_unavailable"]);
        }
        catch (IOException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_unavailable"]);
        }
        catch (InvalidOperationException)
        {
            return new ConfigurationLoadResult(null, fullPath, ["config_file_values_invalid"]);
        }
    }

    private static bool IsWithin(string candidatePath, string directoryPath)
    {
        var directory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }
}
