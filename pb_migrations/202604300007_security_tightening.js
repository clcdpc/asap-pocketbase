
migrate((app) => {
    const collections = [
        "system_settings",
        "ui_settings",
        "workflow_settings",
        "material_formats",
        "audience_groups",
        "email_templates"
    ];

    collections.forEach((name) => {
        try {
            const collection = app.findCollectionByNameOrId(name);
            // Tighten system-wide settings to super_admin for viewing/listing
            // Note: Individual library overrides are handled via custom routes or can be viewed by scoped staff
            // But direct collection access should be restricted to prevent info disclosure.
            collection.listRule = "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'";
            collection.viewRule = "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'";
            app.save(collection);
        } catch (err) {
            console.log("Migration warning: collection " + name + " not found or could not be updated.");
        }
    });

    // Special case for polaris and smtp settings which should ALWAYS be super_admin
    try {
        const polaris = app.findCollectionByNameOrId("polaris_settings");
        polaris.listRule = "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'";
        polaris.viewRule = "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'";
        app.save(polaris);

        const smtp = app.findCollectionByNameOrId("smtp_settings");
        smtp.listRule = "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'";
        smtp.viewRule = "@request.auth.collectionName = 'staff_users' && @request.auth.role = 'super_admin'";
        app.save(smtp);
    } catch (err) {}

}, (app) => {
    // Rollback: return to standard staff list permission for non-secret collections
    const collections = ["system_settings", "ui_settings", "workflow_settings", "material_formats", "audience_groups", "email_templates"];
    collections.forEach((name) => {
        try {
            const collection = app.findCollectionByNameOrId(name);
            collection.listRule = "@request.auth.collectionName = 'staff_users'";
            collection.viewRule = "@request.auth.collectionName = 'staff_users'";
            app.save(collection);
        } catch (err) {}
    });
});
