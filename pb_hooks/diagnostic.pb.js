routerAdd("GET", "/api/asap/diag/jobs/hold-check", (e) => {
    const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
    if (!routeUtils.requireSuperAdminStaff(e)) {
        return e.json(403, { message: "Super admin access required" });
    }
    const jobs = require(`${__hooks}/../lib/jobs.js`);
    try {
        const result = jobs.runScheduledHoldCheck(e.app);
        return e.json(200, { success: true, result: result });
    } catch (err) {
        return e.json(400, { success: false, error: err.message });
    }
});

routerAdd("GET", "/api/asap/diag/polaris/login", (e) => {
    const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
    if (!routeUtils.requireSuperAdminStaff(e)) {
        return e.json(403, { message: "Super admin access required" });
    }
    const polaris = require(`${__hooks}/../lib/polaris.js`);
    try {
        const auth = polaris.adminStaffAuth();
        return e.json(200, { success: true, auth: auth });
    } catch (err) {
        return e.json(400, { success: false, error: err.message });
    }
});

routerAdd("GET", "/api/asap/diag/hmac", (e) => {
    const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
    if (!routeUtils.requireSuperAdminStaff(e)) {
        return e.json(403, { message: "Super admin access required" });
    }
    const crypto = require(`${__hooks}/../lib/crypto.js`);
    const key = "test-key";
    const msg = "test-message";
    const result = crypto.hmacSha1Base64(key, msg);
    return e.json(200, {
        key: key,
        msg: msg,
        hmac: result
    });
});

routerAdd("GET", "/api/asap/diag/polaris", (e) => {
    const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
    if (!routeUtils.requireSuperAdminStaff(e)) {
        return e.json(403, { message: "Super admin access required" });
    }
    const config = require(`${__hooks}/../lib/config.js`);
    const polaris = config.polaris();
    return e.json(200, {
        host: polaris.host,
        accessId: polaris.accessId,
        apiKeySet: !!polaris.apiKey,
        adminUser: polaris.adminUser,
        adminPasswordSet: !!polaris.adminPassword,
        orgId: polaris.orgId,
        appId: polaris.appId,
        langId: polaris.langId,
        serverTime: new Date().toISOString(),
        serverUtc: new Date().toUTCString()
    });
});

routerAdd("GET", "/api/asap/diag/branding", (e) => {
    const routeUtils = require(`${__hooks}/../lib/route_utils.js`);
    if (!routeUtils.requireSuperAdminStaff(e)) {
        return e.json(403, { message: "Super admin access required" });
    }
    const app = $app;
    const collections = app.findCollections();
    const results = [];
    for (let i = 0; i < collections.length; i++) {
        const c = collections[i];
        let hasLogo = false;
        const fields = [];
        for (let j = 0; j < c.fields.length; j++) {
            const f = c.fields[j];
            fields.push(f.name);
            if (f.name === "logo") hasLogo = true;
        }
        if (hasLogo) {
            results.push({
                name: c.name,
                fields: fields
            });
        }
    }
    return e.json(200, results);
});
