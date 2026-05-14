const config = require(`${__hooks}/../lib/config.js`);

function initQueueResult(result, queueName, limits) {
  if (!result.queues) result.queues = {};
  if (!result.queueScanned) result.queueScanned = 0;
  if (!result.maxPerRunReached) result.maxPerRunReached = false;
  if (!result.queues[queueName]) {
    result.queues[queueName] = {
      pageSize: limits.pageSize,
      maxPerRun: limits.maxPerRun,
      scanned: 0,
      pages: 0,
      maxPerRunReached: false,
      moreRemain: false,
    };
  }
  return result.queues[queueName];
}

function cursorFilter(baseFilter, sortField, cursor) {
  if (!cursor || !cursor.value) return baseFilter;
  return "(" + baseFilter + ") && (" + sortField + " > {:cursorValue} || (" + sortField + " = {:cursorValue} && id > {:cursorId}))";
}

function cursorParams(baseParams, cursor) {
  var params = {};
  Object.keys(baseParams || {}).forEach(function (key) {
    params[key] = baseParams[key];
  });
  if (cursor && cursor.value) {
    params.cursorValue = cursor.value;
    params.cursorId = cursor.id || "";
  }
  return params;
}

function logInfo(app, message) {
  try {
    app.logger().info.apply(app.logger(), arguments.length > 2 ? Array.prototype.slice.call(arguments, 1) : [message]);
  } catch (err) {}
}

function processPagedQueue(app, result, options, processRecord) {
  var queueName = options.queueName;
  var limits = config.jobLimits(queueName);
  var queueResult = initQueueResult(result, queueName, limits);
  var scanned = 0;
  var cursor = null;
  var processedStillEligible = false;

  logInfo(app, "ASAP queue processing started", "queue", queueName, "limits", JSON.stringify(limits));

  while (scanned < limits.maxPerRun) {
    var batchSize = Math.min(limits.pageSize, limits.maxPerRun - scanned);
    var page = app.findRecordsByFilter(
      options.collection,
      cursorFilter(options.filter, options.sortField, cursor),
      options.sortField + ",id",
      batchSize,
      0,
      cursorParams(options.params || {}, cursor)
    );

    if (!page.length) break;
    queueResult.pages++;

    for (var i = 0; i < page.length && scanned < limits.maxPerRun; i++) {
      var record = page[i];
      cursor = {
        value: String(record.get(options.sortField) || ""),
        id: String(record.id || ""),
      };
      scanned++;
      queueResult.scanned++;
      result.queueScanned++;
      processRecord(record);
      if (options.params && options.params.status && record.get("status") === options.params.status) {
        processedStillEligible = true;
      }
    }
  }

  if (scanned >= limits.maxPerRun) {
    queueResult.maxPerRunReached = true;
    result.maxPerRunReached = true;
    try {
      var remaining = app.findRecordsByFilter(
        options.collection,
        cursorFilter(options.filter, options.sortField, cursor),
        options.sortField + ",id",
        1,
        0,
        cursorParams(options.params || {}, cursor)
      );
      queueResult.moreRemain = processedStillEligible || remaining.length > 0;
    } catch (err) {
      queueResult.moreRemain = processedStillEligible ? true : null;
    }
  }

  logInfo(app, "ASAP queue processing completed", "queue", queueName, "stats", JSON.stringify(queueResult));
  return queueResult;
}

module.exports = {
  processPagedQueue: processPagedQueue,
};
