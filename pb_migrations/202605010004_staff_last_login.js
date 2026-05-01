/// <reference path="../pb_data/types.d.ts" />

function field(name, type, options) {
  options = options || {};
  options.name = name;
  options.type = type;
  return options;
}

migrate((app) => {
  const staffUsers = app.findCollectionByNameOrId("staff_users");
  try {
    staffUsers.fields.add(new Field(field("lastLogin", "date")));
    app.save(staffUsers);
  } catch (err) {}
}, (app) => {
  return null;
});
