var ghpages = require('gh-pages');

// create an empty gitignore file in the build directory
var fs = require('fs');
var path = require('path');
var buildPath = path.join(__dirname, '../Build/BoidsUnity');
fs.writeFileSync(path.join(buildPath, '.gitignore'), '');

ghpages.publish('../Build/BoidsUnity', (err) =>{
  console.log(err);
});