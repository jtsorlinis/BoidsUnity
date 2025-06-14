var ghpages = require('gh-pages');

ghpages.publish('../Build/BoidsUnity',{
  remove: ['.', '.*', ".vscode"]
},(err) =>{
  console.log(err);
});