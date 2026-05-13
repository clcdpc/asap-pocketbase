const assert = require("assert");
const fs = require("fs");
const path = require("path");

const source = fs.readFileSync(path.join(__dirname, "../pb_public/staff/js/settings-templates.js"), "utf8")
  .replace(/\bexport\s+/g, "");

function extractFunction(name) {
  const start = source.indexOf("function " + name + "(");
  if (start < 0) throw new Error("Could not find function " + name);
  const bodyStart = source.indexOf("{", start);
  let depth = 0;
  for (let i = bodyStart; i < source.length; i++) {
    if (source[i] === "{") depth++;
    if (source[i] === "}") depth--;
    if (depth === 0) return source.slice(start, i + 1);
  }
  throw new Error("Could not extract function " + name);
}

function loadToggle() {
  const helperSource = [
    extractFunction("accordionHeaderForItem"),
    extractFunction("toggleAccordion")
  ].join("\n\n");
  return new Function(helperSource + "\nreturn toggleAccordion;")();
}

class FakeClassList {
  constructor(classes) {
    this.classes = new Set(classes || []);
  }
  contains(name) {
    return this.classes.has(name);
  }
  add(name) {
    this.classes.add(name);
  }
  remove(name) {
    this.classes.delete(name);
  }
  toggle(name, force) {
    const enabled = force === undefined ? !this.classes.has(name) : !!force;
    if (enabled) {
      this.add(name);
    } else {
      this.remove(name);
    }
  }
}

class FakeElement {
  constructor(classes, attrs) {
    this.classList = new FakeClassList(classes);
    this.attrs = Object.assign({}, attrs || {});
    this.children = [];
    this.parent = null;
  }
  append(...children) {
    children.forEach(child => {
      child.parent = this;
      this.children.push(child);
    });
    return this;
  }
  getAttribute(name) {
    return this.attrs[name] || "";
  }
  setAttribute(name, value) {
    this.attrs[name] = String(value);
  }
  closest(selector) {
    const className = selector.charAt(0) === "." ? selector.slice(1) : selector;
    let current = this;
    while (current) {
      if (current.classList.contains(className)) return current;
      current = current.parent;
    }
    return null;
  }
  querySelector(selector) {
    if (selector !== ".asap-accordion-header") return null;
    return this.findByClass("asap-accordion-header");
  }
  findByClass(className) {
    for (const child of this.children) {
      if (child.classList.contains(className)) return child;
      const match = child.findByClass(className);
      if (match) return match;
    }
    return null;
  }
}

function element(classes, attrs) {
  return new FakeElement(classes, attrs);
}

function expanded(item) {
  return item.querySelector(".asap-accordion-header").getAttribute("aria-expanded") === "true";
}

console.log("Running settings accordion behavior tests...");

global.setTimeout = fn => fn();
let toggleAccordion = loadToggle();
let single = element(["asap-accordion"]);
let one = element(["asap-accordion-item", "active"]).append(
  element(["asap-accordion-header"], { "aria-expanded": "true" }),
  element(["asap-accordion-panel"])
);
let two = element(["asap-accordion-item"]).append(
  element(["asap-accordion-header"], { "aria-expanded": "false" }),
  element(["asap-accordion-panel"])
);
single.append(one, two);
toggleAccordion(two);
assert.ok(!one.classList.contains("active"), "single-open accordion should close sibling items");
assert.ok(!expanded(one), "single-open accordion should collapse sibling headers");
assert.ok(two.classList.contains("active"), "clicked item should open");
assert.ok(expanded(two), "clicked item header should expand");

toggleAccordion = loadToggle();
let rowAccordion = element(["asap-accordion"]);
let rowItem = element(["asap-accordion-item"]).append(
  element(["asap-accordion-header-row"]).append(
    element(["asap-accordion-header"], { "aria-expanded": "false" })
  ),
  element(["asap-accordion-panel"])
);
rowAccordion.append(rowItem);
toggleAccordion(rowItem);
assert.ok(rowItem.classList.contains("active"), "accordion items with header rows should still open");
assert.ok(expanded(rowItem), "accordion items with header rows should expand their header");

toggleAccordion = loadToggle();
let multi = element(["asap-accordion"], { "data-accordion-multiple": "true" });
let multiOne = element(["asap-accordion-item", "active"]).append(
  element(["asap-accordion-header"], { "aria-expanded": "true" }),
  element(["asap-accordion-panel"])
);
let multiTwo = element(["asap-accordion-item"]).append(
  element(["asap-accordion-header"], { "aria-expanded": "false" }),
  element(["asap-accordion-panel"])
);
multi.append(multiOne, multiTwo);
toggleAccordion(multiTwo);
assert.ok(multiOne.classList.contains("active"), "multi-open accordion should preserve open siblings");
assert.ok(expanded(multiOne), "multi-open accordion should preserve sibling headers");
assert.ok(multiTwo.classList.contains("active"), "multi-open accordion should open clicked item");

toggleAccordion = loadToggle();
let outer = element(["asap-accordion"], { "data-accordion-multiple": "true" });
let parent = element(["asap-accordion-item", "active"]);
let parentPanel = element(["asap-accordion-panel"]);
let nested = element(["asap-accordion"]);
let nestedOne = element(["asap-accordion-item", "active"]).append(
  element(["asap-accordion-header"], { "aria-expanded": "true" }),
  element(["asap-accordion-panel"])
);
let nestedTwo = element(["asap-accordion-item"]).append(
  element(["asap-accordion-header"], { "aria-expanded": "false" }),
  element(["asap-accordion-panel"])
);
nested.append(nestedOne, nestedTwo);
parentPanel.append(nested);
parent.append(element(["asap-accordion-header"], { "aria-expanded": "true" }), parentPanel);
outer.append(parent);
toggleAccordion(nestedTwo);
assert.ok(parent.classList.contains("active"), "nested accordion interaction should not close its parent item");
assert.ok(expanded(parent), "nested accordion interaction should not collapse its parent header");
assert.ok(!nestedOne.classList.contains("active"), "nested single-open accordion should still close nested siblings");
assert.ok(nestedTwo.classList.contains("active"), "nested clicked item should open");

console.log("All settings accordion behavior tests passed!");
