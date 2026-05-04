routerAdd("GET", "/api/asap/diag/branding", (e) => {
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
