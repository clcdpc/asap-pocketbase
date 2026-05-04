
const app = $app;
const orgs = app.findRecordsByFilter("polaris_organizations", "organizationId != ''", "", 10, 0);
console.log("Found " + orgs.length + " organizations");
orgs.forEach(org => {
    console.log("ID: " + org.get("organizationId") + ", PB ID: " + org.id + ", Name: " + org.get("name"));
});
