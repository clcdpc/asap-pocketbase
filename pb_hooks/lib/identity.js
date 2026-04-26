function normalizeUsername(value) {
  return String(value || "").trim().toLowerCase();
}

function normalizeDomain(value) {
  return String(value || "").trim().toLowerCase();
}

function parseStaffIdentity(value, defaultDomain) {
  var raw = String(value || "").trim();
  var domain = "";
  var authDomain = "";
  var username = raw;

  var slashIndex = raw.indexOf("\\");
  if (slashIndex > 0) {
    authDomain = raw.slice(0, slashIndex).trim();
    domain = authDomain;
    username = raw.slice(slashIndex + 1);
  } else {
    var atIndex = raw.indexOf("@");
    if (atIndex > 0) {
      username = raw.slice(0, atIndex);
      authDomain = raw.slice(atIndex + 1).trim();
      domain = authDomain;
    } else {
      authDomain = String(defaultDomain || "").trim();
      domain = authDomain;
    }
  }

  username = normalizeUsername(username);
  domain = normalizeDomain(domain);

  return {
    username: username,
    domain: domain,
    authDomain: authDomain,
    identityKey: buildIdentityKey(domain, username),
    display: displayIdentity(domain, username),
  };
}

function buildIdentityKey(domain, username) {
  username = normalizeUsername(username);
  domain = normalizeDomain(domain);
  return domain ? domain + "\\" + username : username;
}

function displayIdentity(domain, username) {
  username = normalizeUsername(username);
  domain = normalizeDomain(domain);
  return domain ? domain.toUpperCase() + "\\" + username : username;
}

function parseAllowedStaffUsers(value, defaultDomain) {
  var seen = {};
  var result = [];
  String(value || "").split(",").forEach(function (item) {
    var identity = parseStaffIdentity(item, defaultDomain);
    if (!identity.username || !identity.identityKey || seen[identity.identityKey]) {
      return;
    }
    seen[identity.identityKey] = true;
    result.push(identity.identityKey);
  });
  return result;
}

module.exports = {
  buildIdentityKey: buildIdentityKey,
  displayIdentity: displayIdentity,
  normalizeDomain: normalizeDomain,
  normalizeUsername: normalizeUsername,
  parseAllowedStaffUsers: parseAllowedStaffUsers,
  parseStaffIdentity: parseStaffIdentity,
};
