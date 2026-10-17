var path = require('path');
var ghpages = require('gh-pages');

ghpages.publish(path.join(__dirname, '../Build/BoidsUnity'), {
  remove: ['.', '.*', ".vscode"]
}, (err) => {
  if (err) {
    console.error(err);
    process.exit(1);
  }
  console.log('Published to gh-pages');
});
