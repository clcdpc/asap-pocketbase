
const config = require('./lib/config.js');

try {
  const polaris = config.polaris();
  console.log("Polaris Config (redacted):");
  console.log("Host: " + polaris.host);
  console.log("Access ID: " + polaris.accessId);
  console.log("API Key: " + (polaris.apiKey ? "[SET]" : "[EMPTY]"));
  console.log("Admin User: " + polaris.adminUser);
  console.log("Admin Password: " + (polaris.adminPassword ? "[SET]" : "[EMPTY]"));
  console.log("Org ID: " + polaris.orgId);
  console.log("App ID: " + polaris.appId);
  console.log("Lang ID: " + polaris.langId);
} catch (err) {
  console.log("Error reading Polaris config: " + err);
}
