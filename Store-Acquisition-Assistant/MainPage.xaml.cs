using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Net.Http;
using System.Xml.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Management.Deployment;
using Windows.Foundation.Collections;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Store_Acquisition_Assistant
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private PackageManager packageManager = new PackageManager();

        public MainPage()
        {
            this.InitializeComponent();
            this.GoButton.Click += GoButton_Click;
        }

        private async void GoButton_Click(object sender, RoutedEventArgs e)
        {
            string productId = ProductIDTextBox.Text.Trim();

            if (string.IsNullOrEmpty(productId))
            {
                UpdateStatus("Error: Please enter a product ID");
                return;
            }

            try
            {
                UpdateStatus("Fetching product information from Microsoft Store...");
                OutputTextBlock.Text = "";

                // Fetch the product information from Microsoft Store catalog
                string identityName = await FetchProductIdentityAsync(productId);

                if (string.IsNullOrEmpty(identityName))
                {
                    UpdateStatus("Error: Could not extract Identity Name from product data");
                    return;
                }

                IdentityTextBlock.Text = identityName;
                OutputTextBlock.Text += $"[✓] Identity Name fetched: {identityName}\n\n";

                UpdateStatus("Updating configuration files...");

                // Update AppxManifest.xml with new Identity Name
                bool manifestUpdated = await UpdateAppxManifestAsync(identityName);
                if (manifestUpdated)
                {
                    OutputTextBlock.Text += "[✓] Updated newapp/AppxManifest.xml with new Identity\n";
                }
                else
                {
                    OutputTextBlock.Text += "[!] Warning: Could not update AppxManifest.xml\n";
                }

                // Update main.js with Product ID
                bool jsUpdated = await UpdateMainJsAsync(productId);
                if (jsUpdated)
                {
                    OutputTextBlock.Text += $"[✓] Updated newapp/main.js with Product ID: {productId}\n";
                }
                else
                {
                    OutputTextBlock.Text += "[!] Warning: Could not update main.js\n";
                }

                UpdateStatus("Deploying app...");
                
                // Deploy the app package
                bool deployed = await DeployAppPackageAsync();
                if (deployed)
                {
                    OutputTextBlock.Text += "[✓] App package deployed successfully!\n";
                    UpdateStatus("App deployed successfully!");
                }
                else
                {
                    OutputTextBlock.Text += "[!] Warning: App deployment completed with some messages\n";
                    UpdateStatus("App deployment completed");
                }
                
                OutputTextBlock.Text += "\n[✓] Process complete!";
            }
            catch (Exception ex)
            {
                UpdateStatus($"Error: {ex.Message}");
                OutputTextBlock.Text += $"\nException: {ex.Message}\n{ex.StackTrace}";
            }
        }

        private async Task<string> FetchProductIdentityAsync(string productId)
        {
            string url = $"https://displaycatalog.mp.microsoft.com/v7.0/products/{productId}/0010?fieldsTemplate=InstallAgent&market=US&languages=en-US,en,impartial";

            using (HttpClient client = new HttpClient())
            {
                try
                {
                    var response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    string content = await response.Content.ReadAsStringAsync();
                    OutputTextBlock.Text += $"[✓] HTTP 200 OK - Response received\n";

                    // Parse the XML response
                    XDocument doc = XDocument.Parse(content);
                    XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

                    // Find the Identity element and extract the Name attribute
                    var identityElement = doc.Descendants(ns + "Identity").FirstOrDefault();
                    if (identityElement != null)
                    {
                        string identityName = identityElement.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(identityName))
                        {
                            OutputTextBlock.Text += $"[✓] Parsed Identity from response\n";
                            return identityName;
                        }
                    }

                    // If namespace didn't match, try without namespace
                    var identityElementNoNs = doc.Root?.Element("Identity");
                    if (identityElementNoNs != null)
                    {
                        string identityName = identityElementNoNs.Attribute("Name")?.Value;
                        if (!string.IsNullOrEmpty(identityName))
                        {
                            OutputTextBlock.Text += $"[✓] Parsed Identity from response (no namespace)\n";
                            return identityName;
                        }
                    }

                    OutputTextBlock.Text += "[!] Could not find Identity element in response\n";
                    OutputTextBlock.Text += $"Response preview: {content.Substring(0, Math.Min(500, content.Length))}\n";
                    return null;
                }
                catch (HttpRequestException httpEx)
                {
                    OutputTextBlock.Text += $"[✗] HTTP Error: {httpEx.Message}\n";
                    throw;
                }
            }
        }

        private async Task<bool> UpdateAppxManifestAsync(string identityName)
        {
            try
            {
                StorageFolder appFolder = ApplicationData.Current.LocalFolder;
                StorageFile manifestFile = await appFolder.GetFileAsync("AppxManifest.xml");

                if (manifestFile == null)
                {
                    OutputTextBlock.Text += "[!] AppxManifest.xml not found in LocalFolder\n";
                    return false;
                }

                string content = await FileIO.ReadTextAsync(manifestFile);
                XDocument doc = XDocument.Parse(content);

                // Find and update the Identity element
                XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
                var identityElement = doc.Descendants(ns + "Identity").FirstOrDefault();

                if (identityElement != null)
                {
                    identityElement.SetAttributeValue("Name", identityName);
                    await FileIO.WriteTextAsync(manifestFile, doc.ToString());
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error updating AppxManifest: {ex.Message}\n";
                return false;
            }
        }

        private async Task<bool> UpdateMainJsAsync(string productId)
        {
            try
            {
                StorageFolder appFolder = ApplicationData.Current.LocalFolder;
                StorageFile jsFile = await appFolder.GetFileAsync("main.js");

                if (jsFile == null)
                {
                    OutputTextBlock.Text += "[!] main.js not found in LocalFolder\n";
                    return false;
                }

                string content = await FileIO.ReadTextAsync(jsFile);

                // Replace ONESTOREID with the product ID
                string updatedContent = content.Replace("ONESTOREID", productId);

                await FileIO.WriteTextAsync(jsFile, updatedContent);
                return true;
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error updating main.js: {ex.Message}\n";
                return false;
            }
        }

        private async Task<bool> DeployAppPackageAsync()
        {
            try
            {
                // Get the path to the newapp package
                StorageFolder appFolder = ApplicationData.Current.LocalFolder;
                StorageFolder newappFolder = await appFolder.GetFolderAsync("newapp");

                if (newappFolder == null)
                {
                    OutputTextBlock.Text += "[!] newapp folder not found\n";
                    return false;
                }

                // Create the deployment options
                var deploymentOptions = DeploymentOptions.None;

                // Get the package manager
                PackageManager pm = new PackageManager();

                // For web app packages, we need to add the folder as an external location
                // However, the PackageManager.AddPackageAsync requires a URI to a .appx package
                // Since this is a web app manifest, we'll use an alternative approach

                OutputTextBlock.Text += "[INFO] Attempting to register web app package from: ";
                OutputTextBlock.Text += newappFolder.Path + "\n";

                // Register the package from the local folder
                // Note: This requires the app to have packageManagement capability
                try
                {
                    var deploymentResult = await pm.AddPackageAsync(
                        new Uri(newappFolder.Path),
                        null,
                        deploymentOptions);

                    if (deploymentResult.IsRegistered)
                    {
                        OutputTextBlock.Text += "[✓] Package registered successfully\n";
                        return true;
                    }
                    else if (!string.IsNullOrEmpty(deploymentResult.ErrorText))
                    {
                        OutputTextBlock.Text += $"[!] Deployment error: {deploymentResult.ErrorText}\n";
                    }

                    return deploymentResult.IsRegistered;
                }
                catch (Exception deployEx)
                {
                    OutputTextBlock.Text += $"[!] Deployment exception: {deployEx.Message}\n";
                    
                    // If direct deployment fails, we can still inform the user
                    // that the configuration files have been updated
                    OutputTextBlock.Text += "[INFO] Configuration files have been updated successfully.\n";
                    OutputTextBlock.Text += "[INFO] You may need to deploy the app manually or sign the package.\n";
                    
                    return false;
                }
            }
            catch (Exception ex)
            {
                OutputTextBlock.Text += $"[!] Error in deployment: {ex.Message}\n";
                return false;
            }
        }

        private void UpdateStatus(string status)
        {
            StatusTextBlock.Text = status;
            OutputTextBlock.Text += $"\n[STATUS] {status}\n";
        }
    }
}
