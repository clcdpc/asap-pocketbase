const config = require(`${__hooks}/../lib/config.js`);
const orgs = require(`${__hooks}/../lib/orgs.js`);
const routeUtils = require(`${__hooks}/../lib/route_utils.js`);

function publicConfig(e) {
  try {
    const orgId = routeUtils.queryValue(e, "libraryOrgId") || "";
    var settings = orgId ? config.librarySettings(e.app, orgId) : config.getSettings(e.app);

    var response = settings.ui_text || {};
    var wf = settings.workflow || settings;

    response.commonAuthorsEnabled = !!wf.commonAuthorsEnabled;
    response.commonAuthorsList = wf.commonAuthorsList || "";
    response.commonAuthorsLabel = wf.commonAuthorsLabel || "Popular Creators";
    response.commonAuthorsHelp = wf.commonAuthorsHelp || "See if this is a creator we already collect.";
    response.commonAuthorsMessage = wf.commonAuthorsMessage || "We automatically purchase all upcoming titles by this creator. Please check the catalog to place a hold on 'On Order' items.";
    response.externalSearch1Enabled = !!wf.externalSearch1Enabled;
    response.externalSearch1Label = wf.externalSearch1Label || "Search Amazon";
    response.externalSearch1UrlTemplate = wf.externalSearch1UrlTemplate || "https://www.amazon.com/s?k={{title}}";
    response.externalSearch2Enabled = !!wf.externalSearch2Enabled;
    response.externalSearch2Label = wf.externalSearch2Label || "Search Goodreads";
    response.externalSearch2UrlTemplate = wf.externalSearch2UrlTemplate || "https://www.goodreads.com/search?q={{title}}";
    response.externalSearch3Enabled = !!wf.externalSearch3Enabled;
    response.externalSearch3Label = wf.externalSearch3Label || "Search WorldCat";
    response.externalSearch3UrlTemplate = wf.externalSearch3UrlTemplate || "https://www.worldcat.org/search?q={{title}}";
    response.externalSearch4Enabled = !!wf.externalSearch4Enabled;
    response.externalSearch4Label = wf.externalSearch4Label || "";
    response.externalSearch4UrlTemplate = wf.externalSearch4UrlTemplate || "";

    // Participation check
    if (orgId) {
      var appSettings = config.getSettings(e.app);
      var enabledLibraries = String(appSettings.enabledLibraryOrgIds || "").trim();
      if (enabledLibraries) {
        var enabledList = enabledLibraries.split(",").map(function (id) { return id.trim(); }).filter(function (id) { return id.length > 0; });
        if (enabledList.length > 0 && enabledList.indexOf(orgId) < 0) {
          response.systemNotEnabled = true;
          var msg = response.systemNotEnabledMessage || "{{library}} does not currently participate in this suggestion service.";
          var org = orgs.findOrganization(e.app, orgId);
          var libraryName = org ? String(org.get("displayName") || org.get("name") || "Your library") : "Your library";
          msg = msg.replace(/\{\{library\}\}/g, libraryName);
          if (msg.indexOf("Your library") >= 0) {
            msg = msg.replace("Your library", libraryName);
          }
          response.systemNotEnabledMessage = msg;
        }
      }
    }

    return e.json(200, response);
  } catch (err) {
    e.app.logger().error("Config API Error", "error", String(err));
    return e.json(400, { message: String(err) });
  }
}

module.exports = {
  publicConfig: publicConfig
};
