﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using static AzureSignTool.HRESULT;

namespace AzureSignTool
{
    class Program
    {
        static int Main(string[] args)
        {
            var loggingConfiguration = new ConfigurationBuilder()
                                       .SetBasePath(Path.GetDirectoryName(typeof(Program).Assembly.Location))
                                       .AddJsonFile("logging.json", optional: false, reloadOnChange: true)
                                       .Build();

            var maxLevel = LogLevel.Information;

            var serviceCollection = new ServiceCollection()
                .AddLogging(builder =>
                {
                    builder
                        .AddConfiguration(loggingConfiguration.GetSection("Logging"))
                        .AddConsole()
                        .AddFilter(level => level >= maxLevel);
                        
                });

            // providers may be added to a LoggerFactory before any loggers are created
            

            var serviceProvider = serviceCollection.BuildServiceProvider();
            var logger = serviceProvider.GetRequiredService<ILogger<Program>>();

            LoggerServiceLocator.Current = logger;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Console.Error.WriteLine("Azure Sign Tool is only supported on Windows.");
                return E_PLATFORMNOTSUPPORTED;
            }

            var application = new CommandLineApplication(throwOnUnexpectedArg: false)
            {
                Name = "azuresigntool",
                FullName = "Azure Sign Tool",
            };

            var signCommand = application.Command("sign", throwOnUnexpectedArg: false, configuration: cfg =>
            {
                cfg.Description = "Signs a file.";
                var fileDigestAlgorithm = cfg.Option("-fd | --file-digest", "The digest algorithm to hash the file with.", CommandOptionType.SingleValue);

                var azureKeyVaultUrl = cfg.Option("-kvu | --azure-key-vault-url", "The URL to an Azure Key Vault.", CommandOptionType.SingleValue);
                var azureKeyVaultClientId = cfg.Option("-kvi | --azure-key-vault-client-id", "The Client ID to authenticate to the Azure Key Vault.", CommandOptionType.SingleValue);
                var azureKeyVaultClientSecret = cfg.Option("-kvs | --azure-key-vault-client-secret", "The Client Secret to authenticate to the Azure Key Vault.", CommandOptionType.SingleValue);
                var azureKeyVaultCertificateName = cfg.Option("-kvc | --azure-key-vault-certificate", "The name of the certificate in Azure Key Vault.", CommandOptionType.SingleValue);
                var azureKeyVaultAccessToken = cfg.Option("-kva | --azure-key-vault-accesstoken", "The Access Token to authenticate to the Azure Key Vault.", CommandOptionType.SingleValue);
                var description = cfg.Option("-d | --description", "Provide a description of the signed content.", CommandOptionType.SingleValue);
                var descriptionUrl = cfg.Option("-du | --description-url", "Provide a URL with more information about the signed content.", CommandOptionType.SingleValue);
                var rfc3161TimeStamp = cfg.Option("-tr | --timestamp-rfc3161", "Specifies the RFC 3161 timestamp server's URL. If this option (or -t) is not specified, the signed file will not be timestamped.", CommandOptionType.SingleValue);
                var rfc3161Digest = cfg.Option("-td | --timestamp-digest", "Used with the -tr switch to request a digest algorithm used by the RFC 3161 timestamp server.", CommandOptionType.SingleValue);
                var acTimeStamp = cfg.Option("-t | --timestamp-authenticode", "Specify the timestamp server's URL. If this option is not present, the signed file will not be timestamped.", CommandOptionType.SingleValue);
                var additionalCertificates = cfg.Option("-ac | --additional-certificates", "Specify one or more certificates to include in the public certificate chain.", CommandOptionType.MultipleValue);
                var verbose = cfg.Option("-v | --verbose", "Include additional output.", CommandOptionType.NoValue);
                var quiet = cfg.Option("-q | --quiet", "Do not print any output to the console.", CommandOptionType.NoValue);
                var pageHashing = cfg.Option("-ph | --page-hashing", "Generate page hashes for executable files if supported.", CommandOptionType.NoValue);
                var noPageHashing = cfg.Option("-nph | --no-page-hashing", "Suppress page hashes for executable files if supported.", CommandOptionType.NoValue);
                var continueOnError = cfg.Option("-coe | --continue-on-error", "Continue signing multiple files if an error occurs.", CommandOptionType.NoValue);
                var inputFileList = cfg.Option("-ifl | --input-file-list", "A path to a file that contains a list of files, one per line, to sign.", CommandOptionType.SingleValue);
                var maxDegreeOfParallelism = cfg.Option("-mdop | --max-degree-of-parallelism", "The maximum number of concurrent signing operations.", CommandOptionType.SingleValue);

                var file = cfg.Argument("file", "The path to the file.", multipleValues: true);
                cfg.HelpOption("-? | -h | --help");

                cfg.OnExecute(async () =>
                {

                    var itemScope = 0;

                    X509Certificate2Collection certificates;

                    switch (GetAdditionalCertificates(additionalCertificates.Values))
                    {
                        case ErrorOr<X509Certificate2Collection>.Ok d:
                            certificates = d.Value;
                            break;
                        case ErrorOr<X509Certificate2Collection>.Err err:
                            LoggerServiceLocator.Current.LogError(err.Error, err.Error.Message);
                            return E_INVALIDARG;
                        default:
                            LoggerServiceLocator.Current.LogInformation("Failed to include additional certificates.");
                            return E_INVALIDARG;
                    }

                    if (!CheckMutuallyExclusive(quiet, verbose))
                    {
                        return E_INVALIDARG;
                    }

                    if (quiet.HasValue())
                    {
                        maxLevel = LogLevel.Error;
                    }
                    else if (verbose.HasValue())
                    {
                        maxLevel = LogLevel.Trace;
                    }

                    if (!CheckMutuallyExclusive(acTimeStamp, rfc3161TimeStamp) |
                        !CheckRequired(azureKeyVaultUrl, azureKeyVaultCertificateName) |
                        !CheckMutuallyExclusive(pageHashing, noPageHashing))
                    {
                        return E_INVALIDARG;
                    }
                    if (!azureKeyVaultAccessToken.HasValue() && !CheckRequired(azureKeyVaultClientId, azureKeyVaultClientSecret))
                    {
                        return E_INVALIDARG;
                    }
                    int? signingConcurrency = null;
                    if (maxDegreeOfParallelism.HasValue())
                    {
                        if (int.TryParse(maxDegreeOfParallelism.Value(), out var maxSigningConcurrency) && (maxSigningConcurrency > 0 || maxSigningConcurrency == -1))
                        {
                            signingConcurrency = maxSigningConcurrency;
                        }
                        else
                        {
                            LoggerServiceLocator.Current.LogInformation("Value specified for --max-degree-of-parallelism is not a valid value.");
                            return E_INVALIDARG;
                        }
                    }
                    var listOfFilesToSign = new HashSet<string>();
                    listOfFilesToSign.UnionWith(file.Values);
                    if (inputFileList.HasValue())
                    {
                        if (!File.Exists(inputFileList.Value()))
                        {
                            LoggerServiceLocator.Current.LogInformation($"Input file list {inputFileList.Value()} does not exist.");
                            return E_INVALIDARG;
                        }
                        listOfFilesToSign.UnionWith(File.ReadAllLines(inputFileList.Value()).Where(s => !string.IsNullOrWhiteSpace(s)));
                    }
                    if (listOfFilesToSign.Count == 0)
                    {
                        LoggerServiceLocator.Current.LogError("File or list of files is required.");
                        return E_INVALIDARG;
                    }
                    foreach (var filePath in listOfFilesToSign)
                    {
                        try
                        {
                            if (!File.Exists(filePath))
                            {
                                LoggerServiceLocator.Current.LogInformation($"File {filePath} does not exist or does not have permission.");
                                return E_FILE_NOT_FOUND;
                            }
                        }
                        catch
                        {
                            LoggerServiceLocator.Current.LogInformation($"File {filePath} does not exist or does not have permission.");
                            return E_FILE_NOT_FOUND;
                        }
                    }
                    var configuration = new AzureKeyVaultSignConfigurationSet
                    {
                        AzureKeyVaultUrl = azureKeyVaultUrl.Value(),
                        AzureKeyVaultCertificateName = azureKeyVaultCertificateName.Value(),
                        AzureClientId = azureKeyVaultClientId.Value(),
                        AzureAccessToken = azureKeyVaultAccessToken.Value(),
                        AzureClientSecret = azureKeyVaultClientSecret.Value(),
                        FileDigestAlgorithm = GetValueFromOption(fileDigestAlgorithm, AlgorithmFromInput, HashAlgorithmName.SHA256)
                    };

                    var timestampConfiguration = new TimeStampConfiguration
                    {
                        Url = rfc3161TimeStamp.HasValue() ? rfc3161TimeStamp.Value() :
                              acTimeStamp.HasValue() ? acTimeStamp.Value() : null,
                        Type = rfc3161TimeStamp.HasValue() ? TimeStampType.RFC3161 :
                              acTimeStamp.HasValue() ? TimeStampType.Authenticode : TimeStampType.None,
                        DigestAlgorithm = GetValueFromOption(rfc3161Digest, AlgorithmFromInput, HashAlgorithmName.SHA256)
                    };

                    bool? performPageHashing = null;
                    if (pageHashing.HasValue())
                    {
                        performPageHashing = true;
                    }
                    if (noPageHashing.HasValue())
                    {
                        performPageHashing = false;
                    }

                    var materializedResult = await KeyVaultConfigurationDiscoverer.Materialize(configuration);
                    AzureKeyVaultMaterializedConfiguration materialized;
                    switch (materializedResult)
                    {
                        case ErrorOr<AzureKeyVaultMaterializedConfiguration>.Ok ok:
                            materialized = ok.Value;
                            break;
                        default:
                            LoggerServiceLocator.Current.LogInformation("Failed to get configuration from Azure Key Vault.");
                            return E_INVALIDARG;
                    }
                    int failed = 0, succeeded = 0;
                    var cancellationSource = new CancellationTokenSource();
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        cancellationSource.Cancel();
                        LoggerServiceLocator.Current.LogInformation("Cancelling signing operations.");
                    };
                    using (materialized)
                    {
                        var options = new ParallelOptions();
                        if (signingConcurrency.HasValue)
                        {
                            options.MaxDegreeOfParallelism = signingConcurrency.Value;
                        }
                        Parallel.ForEach(listOfFilesToSign, options, () => (succeeded: 0, failed: 0), (filePath, pls, state) =>
                       {
                          if (cancellationSource.IsCancellationRequested)
                          {
                              pls.Stop();
                          }
                          if (pls.IsStopped)
                          {
                              return state;
                          }
                          var loopLogger = serviceProvider.GetRequiredService<ILogger<Program>>();
                           
                          using (var loopScope = loopLogger.ItemIdScope(Interlocked.Increment(ref itemScope)))
                          {
                              loopLogger.LogTrace("Creating Signer & building chain");

                              using (var signer = new AuthenticodeKeyVaultSigner(materialized, timestampConfiguration, certificates, loopLogger))
                              {
                                  loopLogger.LogInformation($"Signing file {filePath}");
                                  var result = signer.SignFile(filePath, description.Value(), descriptionUrl.Value(), performPageHashing);
                                  switch (result)
                                  {
                                      case COR_E_BADIMAGEFORMAT:
                                          loopLogger.LogError($"The Publisher Identity in the AppxManifest.xml does not match the subject on the certificate for file {filePath}.");
                                          break;
                                  }

                                  if (result == S_OK)
                                  {
                                      loopLogger.LogInformation($"Signing completed successfully for file {filePath}.");
                                      return (state.succeeded + 1, state.failed);
                                  }
                                  else
                                  {
                                      loopLogger.LogError($"Signing failed with error {result:X2} for file {filePath}.");
                                      if (!continueOnError.HasValue() || listOfFilesToSign.Count == 1)
                                      {
                                          loopLogger.LogInformation("Stopping file signing.");
                                          pls.Stop();
                                      }

                                      return (state.succeeded, state.failed + 1);
                                  }
                              }
                          }
                       }, result =>
                      {
                          Interlocked.Add(ref failed, result.failed);
                          Interlocked.Add(ref succeeded, result.succeeded);
                      });
                        LoggerServiceLocator.Current.LogInformation($"Successful operations: {succeeded}");
                        LoggerServiceLocator.Current.LogInformation($"Failed operations: {failed}");
                        if (failed > 0 && succeeded == 0)
                        {
                            return E_ALL_FAILED;
                        }
                        else if (failed > 0)
                        {
                            return S_SOME_SUCCESS;
                        }
                        else
                        {
                            return S_OK;
                        }
                    }
                });
            });
            application.OnExecute(() =>
            {
                application.ShowHelp();
                return E_INVALIDARG;
            });
            return application.Execute(args);
        }

        private static HashAlgorithmName? AlgorithmFromInput(string value)
        {
            switch (value?.ToLower())
            {
                case "sha1":
                    return HashAlgorithmName.SHA1;
                case "sha384":
                    return HashAlgorithmName.SHA384;
                case "sha512":
                    return HashAlgorithmName.SHA512;
                case null:
                case "sha256":
                    return HashAlgorithmName.SHA256;
                default:
                    return null;

            }
        }

        private static ErrorOr<X509Certificate2Collection> GetAdditionalCertificates(IEnumerable<string> paths)
        {
            var collection = new X509Certificate2Collection();
            try
            {
                foreach (var path in paths)
                {

                    var type = X509Certificate2.GetCertContentType(path);
                    switch (type)
                    {
                        case X509ContentType.Cert:
                        case X509ContentType.Authenticode:
                        case X509ContentType.SerializedCert:
                            var certificate = new X509Certificate2(path);
                            LoggerServiceLocator.Current.LogTrace($"Including additional certificate {certificate.Thumbprint}.");
                            collection.Add(certificate);
                            break;
                        default:
                            return new Exception($"Specified file {path} is not a public valid certificate.");
                    }
                }
            }
            catch (CryptographicException e)
            {
                LoggerServiceLocator.Current.LogTrace($"An exception occured while including an additional certificate:\n{e}");
                return e;
            }

            return collection;
        }

        private static bool CheckMutuallyExclusive(params CommandOption[] commands)
        {
            if (commands.Length < 2)
            {
                return true;
            }
            var set = new HashSet<string>(commands.Where(c => c.HasValue()).Select(c => $"-{c.ShortName}"));
            if (set.Count > 1)
            {
                LoggerServiceLocator.Current.LogError($"Cannot use {String.Join(", ", set)} options together.");
                return false;
            }
            return true;
        }

        private static bool CheckRequired(params CommandOption[] commands)
        {
            var set = new HashSet<string>(commands.Where(c => !c.HasValue()).Select(c => $"-{c.ShortName}"));
            if (set.Count > 0)
            {
                LoggerServiceLocator.Current.LogError($"Options {String.Join(", ", set)} are required.");
                return false;
            }
            return true;
        }

        private static T GetValueFromOption<T>(CommandOption option, Func<string, T> transform, T defaultIfNull) where T : class
        {
            if (!option.HasValue())
            {
                return defaultIfNull;
            }
            return transform(option.Value()) ?? defaultIfNull;
        }

        private static T GetValueFromOption<T>(CommandOption option, Func<string, T?> transform, T defaultIfNull) where T : struct
        {
            if (!option.HasValue())
            {
                return defaultIfNull;
            }
            return transform(option.Value()) ?? defaultIfNull;
        }
    }
}
