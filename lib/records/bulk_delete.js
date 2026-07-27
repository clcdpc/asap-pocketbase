function recordId(record) {
  return record && (record.id || (typeof record.get === "function" ? record.get("id") : ""));
}

function deleteRecords(app, collectionName, records) {
  if (!records || !records.length) return 0;

  var ids = [];
  for (var i = 0; i < records.length; i++) {
    var id = recordId(records[i]);
    if (id) ids.push(id);
  }

  if (ids.length && app && typeof app.deleteRecords === "function") {
    app.deleteRecords(collectionName, ids);
    return ids.length;
  }

  var deleted = 0;
  for (var j = 0; j < records.length; j++) {
    if (records[j]) {
      app.delete(records[j]);
      deleted++;
    }
  }
  return deleted;
}

module.exports = {
  deleteRecords: deleteRecords
};
