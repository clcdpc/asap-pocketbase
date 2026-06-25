function split(value) {
  return String(value || "")
    .split(",")
    .map(function (s) { return s.trim(); })
    .filter(function (s) { return s.length > 0; });
}

module.exports = {
  split: split
};
