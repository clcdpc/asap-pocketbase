'use strict';
const assert = require('node:assert/strict');

module.exports = async function verifyMixedFieldLayout(page, container) {
  const rows = await page.locator(container).evaluate(root => {
    const results = [];
    // Exercise the actual shared layout with deliberately different label,
    // helper, validation and native-control heights, including the next row.
    for (const gridClass of ['edit-form', 'settings-grid']) {
      const fixture = document.createElement('div');
      fixture.className = gridClass;
      for (const type of ['date', 'select', 'date', 'text', 'date', 'number']) {
        const label = document.createElement('label');
        label.className = gridClass === 'settings-grid' ? 'settings-field' : 'form-field';
        const caption = document.createElement('span');
        caption.textContent = type === 'date' ? 'Publication date with a deliberately long wrapping label for translated text' : 'Short label';
        const control = document.createElement(type === 'select' ? 'select' : 'input');
        if (type !== 'select') control.type = type;
        control.setAttribute('aria-label', `Layout regression ${type}`);
        const help = document.createElement('div');
        help.className = 'form-field-help';
        help.textContent = type === 'select' || type === 'number'
          ? 'Long helper and validation text belongs below its own control. More lines must not move a neighboring control or overlap the following row.' : '';
        label.append(caption, control, help); fixture.append(label);
      }
      root.append(fixture);
      const fields = [...fixture.children].map(label => ({ label: label.firstChild.getBoundingClientRect().toJSON(),
        control: label.children[1].getBoundingClientRect().toJSON(), bottom: label.getBoundingClientRect().bottom,
        type: label.children[1].type }));
      results.push({ gridClass, fields }); fixture.remove();
    }
    return results;
  });
  for (const { gridClass, fields } of rows) {
    for (let index = 0; index < fields.length; index += 2) {
      const [left, right] = fields.slice(index, index + 2);
      if (Math.abs(left.control.left - right.control.left) > 5) {
        assert.ok(Math.abs(left.label.top - right.label.top) <= 1, `${gridClass}: label tops`);
        assert.ok(Math.abs(left.control.top - right.control.top) <= 1, `${gridClass}: mixed control tops`);
        if (index + 2 < fields.length) assert.ok(fields[index + 2].label.top >= Math.max(left.bottom, right.bottom), `${gridClass}: next row clears help`);
      } else {
        assert.ok(right.label.top >= left.bottom, `${gridClass}: stacked reading order`);
      }
      assert.equal(left.type, 'date', 'The shared layout retains native date controls.');
    }
  }
  return rows;
};
