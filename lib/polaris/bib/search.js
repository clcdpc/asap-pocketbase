const helpers = require("../helpers.js");
const { bibSearchRows } = require("./parse.js");
const { getMaterialTypeDetails, getBibFormatLabel, formatMaterialIconUrl } = require("./material-types.js");

function normalizeBibSearchRow(app, row) {
  row = row || {};
  var primaryTomId = String(row.PrimaryTypeOfMaterial || "").trim();
  var materialType = getMaterialTypeDetails(app, primaryTomId);
  var formatLabel = getBibFormatLabel(app, row);

  return {
    bibId: helpers.firstRowValue(row, ["ControlNumber", "BibID", "BibId", "BibliographicRecordID", "BibliographicRecordId", "RecordID"]),
    title: helpers.firstRowValue(row, ["DisplayTitle", "FullTitle", "Title", "SortTitle"]),
    author: helpers.firstRowValue(row, ["Author", "PrimaryAuthor", "AuthorDisplay", "SortAuthor"]),
    publication: helpers.firstRowValue(row, ["PublicationDate", "PublicationYear", "PublishDate", "PublishedDate", "Date"]),
    format: formatLabel,
    primaryTomId: primaryTomId,
    materialTypeDesc: String(row.MaterialTypeDescription || ""),
    formatIconUrl: materialType ? formatMaterialIconUrl(app, materialType) : "",
    formatIconAlt: materialType ? materialType.description : formatLabel,
    materialTypeSearchCode: materialType ? materialType.searchCode : "",
    physicalDescription: helpers.firstRowValue(row, ["Description"]),
    identifier: helpers.firstRowValue(row, ["ISBN", "ISSN", "UPC", "Identifier"]),
    score: 0
  };
}

function buildBibSearchRequests(options) {
  var mode = String(options.mode || "title").trim().toLowerCase();
  var query = helpers.cleanSearchTerm(options.query);
  var title = helpers.cleanSearchTerm(options.title || query);
  var author = helpers.cleanSearchTerm(options.author);

  switch (mode) {
    case "title":
      return [{ type: "keyword", qualifier: "TI", q: title, sortby: "RELEVANCE" }];

    case "author":
      return [{ type: "keyword", qualifier: "AU", q: author || query, sortby: "AU" }];

    case "identifier":
      var check = helpers.normalizeIdentifier(query);
      var normalized = check.ok ? check.normalized : query;
      return [
        { type: "keyword", qualifier: "ISBN", q: normalized, sortby: "PDTI" },
        { type: "keyword", qualifier: "UPC", q: normalized, sortby: "PDTI" },
        { type: "keyword", qualifier: "LCCN", q: normalized, sortby: "PDTI" }
      ];

    case "title_author":
      var requests = [];
      if (title && author) {
        requests.push({
          type: "boolean",
          q: "TI=" + helpers.cclQuotedValue(title) + " AND AU=" + helpers.cclQuotedValue(author),
          sortby: "PDTI"
        });
      }
      requests.push({
        type: "keyword",
        qualifier: "TI",
        q: title,
        sortby: "RELEVANCE",
        postFilterAuthor: author
      });
      return requests;

    default:
      return [{ type: "keyword", qualifier: "KW", q: query, sortby: "RELEVANCE" }];
  }
}

function scoreBibResult(result, options) {
  var score = 0;
  var targetTitle = helpers.normalizedLabel(options.title || options.query);
  var targetAuthor = helpers.normalizedLabel(options.author);
  var targetId = helpers.normalizeIdentifier(options.query).normalized;

  var rowTitle = helpers.normalizedLabel(result.title);
  var rowAuthor = helpers.normalizedLabel(result.author);
  var rowId = helpers.normalizeIdentifier(result.identifier).normalized;

  if (targetId && rowId && rowId.indexOf(targetId) !== -1) {
    score += 200;
  }

  if (targetTitle && rowTitle === targetTitle) {
    score += 100;
  } else if (targetTitle && rowTitle.indexOf(targetTitle) === 0) {
    score += 40;
  }

  if (targetAuthor && rowAuthor.indexOf(targetAuthor) !== -1) {
    score += 30;
  }

  var year = parseInt(String(result.publication || "").replace(/\D/g, "").substring(0, 4), 10);
  if (year > 1900 && year <= new Date().getFullYear()) {
    score += Math.min(5, (year - 1900) / 20);
  }

  return score;
}

function searchBibs(app, staff, options) {
  options = options || {};
  var limit = parseInt(options.limit || 10, 10) || 10;
  if (limit < 1) limit = 1;
  if (limit > 25) limit = 25;

  var requests = [];
  try {
    requests = buildBibSearchRequests(options);
  } catch (err) {
    return { status: "error", mode: options.mode, query: options.query, bibId: "", multipleMatches: false, totalMatches: 0, results: [], error: err.message };
  }

  var allResults = [];
  var seenBibIds = {};
  var totalMatches = 0;
  var filteredByMaterialType = false;

  for (var r = 0; r < requests.length; r++) {
    var req = requests[r];
    try {
      var path = req.type === "boolean" ? "search/bibs/boolean" : ("search/bibs/keyword/" + (req.qualifier || "KW"));
      var ep = helpers.endpoint("public", path);
      var queryParams = "q=" + encodeURIComponent(req.q) + "&sortby=" + (req.sortby || "RELEVANCE") + "&bibsperpage=" + limit + "&page=1&notran=1";
      helpers.appendQuery(ep, queryParams);

      var payload;
      try {
        payload = helpers.send("GET", ep, "", staff);
      } catch (err) {
        if (/\(Code:\s*-1\)/.test(err.message)) continue;
        throw err;
      }

      var rows = bibSearchRows(payload);
      totalMatches = Math.max(totalMatches, Number(payload.TotalRecordsFound || rows.length || 0) || 0);

      var NON_HOLDABLE_TOMS = { "36": true, "41": true };

      for (var i = 0; i < rows.length; i++) {
        var result = normalizeBibSearchRow(app, rows[i]);
        if (!result.bibId || seenBibIds[result.bibId]) continue;
        if (NON_HOLDABLE_TOMS[result.primaryTomId]) {
          filteredByMaterialType = true;
          continue;
        }

        if (req.postFilterAuthor) {
          var targetAuthor = helpers.normalizedLabel(req.postFilterAuthor);
          var rowAuthor = helpers.normalizedLabel(result.author);
          if (rowAuthor.indexOf(targetAuthor) === -1) continue;
        }

        result.score = scoreBibResult(result, options);
        allResults.push(result);
        seenBibIds[result.bibId] = true;
      }

      if (allResults.length >= limit) break;
    } catch (err) {
      if ($app.logger) $app.logger().error("Polaris search request failed", "error", err.message, "request", JSON.stringify(req));
    }
  }

  allResults.sort(function(a, b) {
    return b.score - a.score;
  });

  var results = allResults.slice(0, limit);

  if (!results.length) {
    return { status: "not_found", mode: options.mode, query: options.query, bibId: "", multipleMatches: false, totalMatches: totalMatches, results: [], error: "", filteredByMaterialType: filteredByMaterialType };
  }

  return {
    status: "found",
    mode: options.mode,
    query: options.query,
    bibId: results[0].bibId,
    multipleMatches: totalMatches > 1 || results.length > 1,
    totalMatches: totalMatches,
    results: results,
    error: "",
    filteredByMaterialType: filteredByMaterialType
  };
}

function searchBib(app, staff, identifier) {
  var result = searchBibs(app, staff, { mode: "identifier", query: identifier, limit: 10 });
  return {
    status: result.status,
    bibId: result.bibId,
    multipleMatches: result.multipleMatches,
    totalMatches: result.totalMatches,
    results: result.results || [],
    error: result.error || "",
    filteredByMaterialType: result.filteredByMaterialType || false
  };
}

module.exports = { normalizeBibSearchRow, buildBibSearchRequests, scoreBibResult, searchBibs, searchBib };
